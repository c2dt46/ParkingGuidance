using System.Buffers.Binary;
using System.Net.Sockets;

namespace ParkingGuidance.Web.Services.Modbus;

/// <summary>Thiết bị trả về mã lỗi Modbus (exception response).</summary>
public sealed class ModbusException : Exception
{
    public byte Code { get; }
    public ModbusException(byte code) : base(Describe(code)) => Code = code;

    private static string Describe(byte code) => code switch
    {
        1 => "Thiết bị không hỗ trợ lệnh ghi nhiều thanh ghi (mã 16) — mã lỗi Modbus 01.",
        2 => "Địa chỉ thanh ghi không hợp lệ trên thiết bị — mã lỗi Modbus 02.",
        3 => "Giá trị / số lượng thanh ghi không hợp lệ — mã lỗi Modbus 03.",
        4 => "Thiết bị báo lỗi khi xử lý — mã lỗi Modbus 04.",
        5 or 6 => $"Thiết bị đang bận — mã lỗi Modbus 0{code}.",
        _ => $"Thiết bị trả mã lỗi Modbus {code:00}."
    };
}

/// <summary>
/// Kết nối Modbus TCP tối thiểu cho chế độ Client: chỉ cần lệnh "Write Multiple Registers" (mã 16).
/// Không phụ thuộc thư viện ngoài. Khung dữ liệu: MBAP (7 byte: mã giao dịch, giao thức = 0, độ dài, Unit ID) + PDU.
/// </summary>
internal sealed class ModbusTcpConnection : IDisposable
{
    public const int MaxRegistersPerWrite = 123;          // giới hạn của đặc tả Modbus cho mã 16

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private ushort _transactionId;

    private ModbusTcpConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static async Task<ModbusTcpConnection> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await tcp.ConnectAsync(host, port, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Không kết nối được tới {host}:{port} (quá {timeout.TotalSeconds:0} giây).");
            }
            return new ModbusTcpConnection(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Ghi <paramref name="values"/> vào các thanh ghi liên tiếp bắt đầu từ <paramref name="start"/>.</summary>
    public async Task WriteMultipleRegistersAsync(byte unitId, ushort start, ushort[] values, TimeSpan timeout, CancellationToken ct)
    {
        int qty = values.Length;
        if (qty < 1 || qty > MaxRegistersPerWrite) throw new ArgumentOutOfRangeException(nameof(values));

        var tid = ++_transactionId;
        var req = BuildWriteRequest(tid, unitId, start, values);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _stream.WriteAsync(req, cts.Token);

            var header = new byte[7];
            await ReadExactAsync(header, 7, cts.Token);
            if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0)) != tid)
                throw new IOException("Phản hồi Modbus sai mã giao dịch.");
            if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0)
                throw new IOException("Phản hồi không phải Modbus TCP.");
            int len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));       // = Unit ID (1) + PDU
            if (len < 2 || len > 254) throw new IOException("Độ dài phản hồi Modbus không hợp lệ.");

            var pdu = new byte[len - 1];
            await ReadExactAsync(pdu, pdu.Length, cts.Token);

            if (pdu[0] == (0x10 | 0x80))
                throw new ModbusException(pdu.Length > 1 ? pdu[1] : (byte)0);
            if (pdu[0] != 0x10 || pdu.Length < 5
                || BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1)) != start
                || BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3)) != qty)
                throw new IOException("Phản hồi ghi thanh ghi không khớp yêu cầu.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Thiết bị không phản hồi trong {timeout.TotalSeconds:0} giây.");
        }
    }

    /// <summary>Dựng khung yêu cầu mã 16: MBAP (7) + mã 0x10 + địa chỉ đầu (2) + số thanh ghi (2) + số byte (1) + dữ liệu (2 byte/thanh ghi, big-endian).</summary>
    internal static byte[] BuildWriteRequest(ushort transactionId, byte unitId, ushort start, ushort[] values)
    {
        int qty = values.Length;
        var req = new byte[13 + qty * 2];
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(0), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(2), 0);                         // protocol id
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(4), (ushort)(7 + qty * 2));     // Unit ID + PDU
        req[6] = unitId;
        req[7] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(8), start);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(10), (ushort)qty);
        req[12] = (byte)(qty * 2);
        for (int i = 0; i < qty; i++)
            BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(13 + i * 2), values[i]);
        return req;
    }

    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(off, count - off), ct);
            if (n == 0) throw new IOException("Thiết bị đã đóng kết nối.");
            off += n;
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { /* da dong */ }
        try { _tcp.Dispose(); } catch { /* da dong */ }
    }
}
