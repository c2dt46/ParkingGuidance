using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ParkingGuidance.Web.Services.Modbus;

/// <summary>
/// Bảng 65536 thanh ghi (holding / input dùng chung) của 1 cổng Server. Mặc định toàn 0. Phần đưa dữ liệu vào bảng này
/// (biến MS, tag…) sẽ làm sau qua <see cref="Get"/> / <see cref="Set"/>; master ghi vào cũng nằm trong bảng này.
/// </summary>
public sealed class ModbusRegisterBank
{
    public const int Size = 65536;
    private readonly ushort[] _regs = new ushort[Size];
    private readonly object _l = new();

    public ushort Get(int address) { lock (_l) return _regs[address]; }
    public void Set(int address, ushort value) { lock (_l) _regs[address] = value; }

    internal void Read(int start, int qty, Span<byte> dest)
    {
        lock (_l)
            for (int i = 0; i < qty; i++) BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(i * 2), _regs[start + i]);
    }

    internal void Write(int start, ReadOnlySpan<byte> src)
    {
        lock (_l)
            for (int i = 0; i < src.Length / 2; i++) _regs[start + i] = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(i * 2));
    }
}

/// <summary>Trạng thái của 1 khai báo Server (hiển thị ở trang Thiết bị ngoài).</summary>
public sealed class ModbusServerStatus
{
    public int Id { get; init; }
    public int Port { get; init; }
    /// <summary>Disabled · Starting · Listening · Error</summary>
    public string State { get; init; } = "Disabled";
    public string? Error { get; init; }
    /// <summary>Số kết nối đang mở.</summary>
    public int Connections { get; init; }
    public long TotalConnections { get; init; }
    public long Requests { get; init; }
    public DateTime? LastRequestUtc { get; init; }
}

public interface IModbusServerStatusProvider
{
    IReadOnlyList<ModbusServerStatus> GetAll();
}

/// <summary>
/// Modbus TCP/IP chế độ Server chạy nền, tách riêng khỏi các luồng xử lý chính: mỗi cổng đã bật là 1 tác vụ async riêng
/// (không dùng thread chặn). Trả lời các lệnh đọc thanh ghi (03, 04) và ghi thanh ghi (06, 16) trên bảng
/// <see cref="ModbusRegisterBank"/> của cổng đó; lệnh khác trả mã lỗi 01 (không hỗ trợ).
/// Giới hạn: tối đa 16 kết nối mỗi cổng; kết nối im lặng quá 5 phút bị đóng. Bấm Lưu khai báo thì các cổng tự mở lại theo cấu hình mới
/// (bảng thanh ghi được giữ nguyên). Cổng bị chiếm / thiếu quyền thì báo lỗi ở cột Trạng thái và thử mở lại sau 10 giây.
/// </summary>
public sealed class ModbusServerService : BackgroundService, IModbusServerStatusProvider
{
    private const int MaxConnectionsPerPort = 16;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly IModbusConfigService _cfg;
    private readonly ILogger<ModbusServerService> _logger;
    private readonly object _lock = new();
    private readonly List<Listener> _listeners = new();
    private readonly Dictionary<int, ModbusRegisterBank> _banks = new();
    private CancellationToken _stopping;

    private sealed class Listener
    {
        public required int Id { get; init; }
        public required int Port { get; init; }
        public required ModbusRegisterBank Bank { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        private readonly object _l = new();
        private string _state = "Disabled";
        private string? _error;
        private int _connections;
        private long _total, _requests;
        private long _lastRequestTicks;

        public void Set(string state, string? error = null) { lock (_l) { _state = state; _error = error; } }
        public bool TryEnter()
        {
            lock (_l)
            {
                if (_connections >= MaxConnectionsPerPort) return false;
                _connections++; _total++;
                return true;
            }
        }
        public void Leave() { lock (_l) _connections--; }
        public void OnRequest() { Interlocked.Increment(ref _requests); Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks); }
        public ModbusServerStatus Snapshot()
        {
            lock (_l)
            {
                long t = Interlocked.Read(ref _lastRequestTicks);
                return new ModbusServerStatus
                {
                    Id = Id, Port = Port, State = _state, Error = _error, Connections = _connections,
                    TotalConnections = _total, Requests = Interlocked.Read(ref _requests),
                    LastRequestUtc = t == 0 ? null : new DateTime(t, DateTimeKind.Utc)
                };
            }
        }
    }

    public ModbusServerService(IModbusConfigService cfg, ILogger<ModbusServerService> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    /// <summary>Bảng thanh ghi của 1 Server (để phần dữ liệu sau này ghi vào). Tạo khi cần, giữ nguyên khi cấu hình đổi.</summary>
    public ModbusRegisterBank GetBank(int serverId)
    {
        lock (_lock)
        {
            if (!_banks.TryGetValue(serverId, out var b)) _banks[serverId] = b = new ModbusRegisterBank();
            return b;
        }
    }

    public IReadOnlyList<ModbusServerStatus> GetAll()
    {
        List<Listener> copy;
        lock (_lock) copy = _listeners.ToList();
        return copy.Select(l => l.Snapshot()).OrderBy(s => s.Id).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _cfg.Changed += Apply;
        try
        {
            Apply();
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { /* tat chuong trinh */ }
        finally
        {
            _cfg.Changed -= Apply;
            lock (_lock) StopAllLocked();
        }
    }

    private void Apply()
    {
        lock (_lock)
        {
            StopAllLocked();
            if (_stopping.IsCancellationRequested) return;
            foreach (var entry in _cfg.Get().Servers)
            {
                var l = new Listener { Id = entry.Id, Port = entry.Port, Bank = GetBankLocked(entry.Id) };
                _listeners.Add(l);
                if (!entry.Enabled) { l.Set("Disabled"); continue; }
                l.Set("Starting");
                var token = CancellationTokenSource.CreateLinkedTokenSource(l.Cts.Token, _stopping).Token;
                _ = Task.Run(() => ListenAsync(l, token));
            }
        }
    }

    private ModbusRegisterBank GetBankLocked(int id)
    {
        if (!_banks.TryGetValue(id, out var b)) _banks[id] = b = new ModbusRegisterBank();
        return b;
    }

    private void StopAllLocked()
    {
        foreach (var l in _listeners) { try { l.Cts.Cancel(); } catch { /* da huy */ } }
        _listeners.Clear();
    }

    // ------------------------------------------------------------------ nghe cong
    private async Task ListenAsync(Listener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpListener? tl = null;
            try
            {
                tl = new TcpListener(IPAddress.Any, l.Port);
                tl.Start();
                l.Set("Listening");
                while (!ct.IsCancellationRequested)
                {
                    var client = await tl.AcceptTcpClientAsync(ct);
                    if (!l.TryEnter()) { client.Dispose(); continue; }       // qua so ket noi toi da
                    _ = Task.Run(() => HandleAsync(l, client, ct));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SocketException ex)
            {
                l.Set("Error", ex.SocketErrorCode switch
                {
                    SocketError.AddressAlreadyInUse => $"Cổng {l.Port} đang được chương trình khác sử dụng.",
                    SocketError.AccessDenied => $"Không đủ quyền mở cổng {l.Port} (chạy bằng quyền quản trị hoặc chọn cổng từ 1024 trở lên).",
                    _ => ex.Message
                });
            }
            catch (Exception ex)
            {
                l.Set("Error", ex.Message);
                _logger.LogError(ex, "Modbus server cong {Port} loi.", l.Port);
            }
            finally { try { tl?.Stop(); } catch { /* da dong */ } }

            try { await Task.Delay(RetryDelay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // ------------------------------------------------------------------ 1 ket noi
    private async Task HandleAsync(Listener l, TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (ct.Register(() => { try { client.Close(); } catch { /* da dong */ } }))
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var header = new byte[7];

                while (!ct.IsCancellationRequested)
                {
                    // cho yeu cau tiep theo (im lang qua lau thi dong)
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        idle.CancelAfter(IdleTimeout);
                        if (!await ReadExactAsync(stream, header, 7, idle.Token, allowCleanEof: true)) return;
                    }

                    int len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));           // = Unit ID + PDU
                    if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0 || len < 2 || len > 254) return;   // khong phai Modbus TCP

                    var pdu = new byte[len - 1];
                    using (var frame = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        frame.CancelAfter(FrameTimeout);
                        await ReadExactAsync(stream, pdu, pdu.Length, frame.Token, allowCleanEof: false);
                    }

                    var respPdu = ProcessPdu(l.Bank, pdu);
                    l.OnRequest();

                    var resp = new byte[7 + respPdu.Length];
                    Array.Copy(header, resp, 4);                                                // ma giao dich + giao thuc
                    BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4), (ushort)(1 + respPdu.Length));
                    resp[6] = header[6];                                                        // Unit ID
                    Array.Copy(respPdu, 0, resp, 7, respPdu.Length);

                    using var wr = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wr.CancelAfter(FrameTimeout);
                    await stream.WriteAsync(resp, wr.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException or InvalidOperationException)
        {
            /* master ngat ket noi / het gio / dang tat: khong phai loi cua chuong trinh */
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Modbus server cong {Port}: loi ket noi.", l.Port);
        }
        finally { l.Leave(); }
    }

    /// <summary>Đọc đủ <paramref name="count"/> byte. Trả về false nếu bên kia đóng kết nối sạch trước khi gửi byte nào (khi cho phép).</summary>
    private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, int count, CancellationToken ct, bool allowCleanEof)
    {
        int off = 0;
        while (off < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0)
            {
                if (off == 0 && allowCleanEof) return false;
                throw new IOException("Kết nối bị đóng giữa chừng.");
            }
            off += n;
        }
        return true;
    }

    // ------------------------------------------------------------------ xu ly lenh
    /// <summary>Xử lý 1 PDU Modbus (mã lệnh + dữ liệu) và trả PDU phản hồi. Hỗ trợ 03, 04 (đọc), 06, 16 (ghi); còn lại trả lỗi 01.</summary>
    internal static byte[] ProcessPdu(ModbusRegisterBank bank, byte[] pdu)
    {
        byte fc = pdu[0];
        switch (fc)
        {
            case 0x03:
            case 0x04:
            {
                if (pdu.Length != 5) return Exception(fc, 3);
                int addr = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                int qty = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                if (qty < 1 || qty > 125) return Exception(fc, 3);
                if (addr + qty > ModbusRegisterBank.Size) return Exception(fc, 2);
                var resp = new byte[2 + qty * 2];
                resp[0] = fc;
                resp[1] = (byte)(qty * 2);
                bank.Read(addr, qty, resp.AsSpan(2));
                return resp;
            }
            case 0x06:
            {
                if (pdu.Length != 5) return Exception(fc, 3);
                int addr = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                bank.Set(addr, BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3)));
                return (byte[])pdu.Clone();                                          // phan hoi = lap lai yeu cau
            }
            case 0x10:
            {
                if (pdu.Length < 6) return Exception(fc, 3);
                int addr = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                int qty = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                int bytes = pdu[5];
                if (qty < 1 || qty > 123 || bytes != qty * 2 || pdu.Length != 6 + bytes) return Exception(fc, 3);
                if (addr + qty > ModbusRegisterBank.Size) return Exception(fc, 2);
                bank.Write(addr, pdu.AsSpan(6, bytes));
                return new[] { fc, pdu[1], pdu[2], pdu[3], pdu[4] };
            }
            default:
                return Exception(fc, 1);
        }
    }

    private static byte[] Exception(byte fc, byte code) => new[] { (byte)(fc | 0x80), code };
}
