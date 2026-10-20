using System.Diagnostics;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Camera;
using ParkingGuidance.Web.Services.Sensor;

namespace ParkingGuidance.Web.Services.Modbus;

/// <summary>Trạng thái kết nối + gửi của 1 khai báo Client (hiển thị ở trang Thiết bị ngoài).</summary>
public sealed class ModbusClientStatus
{
    public int Id { get; init; }
    /// <summary>Disabled · Idle (chưa gán biến) · Connecting · Connected · Error</summary>
    public string State { get; init; } = "Disabled";
    public string? Error { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    public long Writes { get; init; }
    public long Failures { get; init; }
    /// <summary>Số thanh ghi ghi được ở lần gần nhất.</summary>
    public int Registers { get; init; }
    /// <summary>Số biến chưa có giá trị (không ghi) ở lần gần nhất.</summary>
    public int Skipped { get; init; }
}

public interface IModbusClientStatusProvider
{
    IReadOnlyList<ModbusClientStatus> GetAll();
}

/// <summary>
/// Modbus TCP/IP chế độ Client chạy nền, TÁCH RIÊNG khỏi các luồng xử lý chính: mỗi thiết bị đã bật là 1 tác vụ async riêng
/// (kết nối, ghi định kỳ, tự kết nối lại có độ trễ tăng dần). Mỗi chu kỳ chỉ lấy giá trị các biến đã gán bằng 1 lần đọc
/// (<see cref="ISensorVariableService.GetValues"/> cho biến MS, <see cref="ICameraVariableService.GetValues"/> cho biến MC) rồi ghi các thanh ghi liên tiếp theo từng khối (mã 16).
/// Khi người dùng bấm Lưu khai báo (<see cref="IModbusConfigService.Changed"/>) thì các tác vụ được khởi động lại theo cấu hình mới.
/// </summary>
public sealed class ModbusClientService : BackgroundService, IModbusClientStatusProvider
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(3);
    private const int MaxBackoffMs = 30_000;

    private readonly IModbusConfigService _cfg;
    private readonly ISensorVariableService _vars;
    private readonly ICameraVariableService _camVars;
    private readonly ILogger<ModbusClientService> _logger;
    private readonly object _lock = new();
    private readonly List<Worker> _workers = new();
    private CancellationToken _stopping;

    private sealed class Worker
    {
        public required ModbusClientEntry Entry { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        private readonly object _l = new();
        private string _state = "Disabled";
        private string? _error;
        private DateTime? _lastWrite;
        private long _writes, _failures;
        private int _registers, _skipped;

        public void Set(string state, string? error = null) { lock (_l) { _state = state; _error = error; } }
        public void OnWrite(int registers, int skipped)
        {
            lock (_l) { _state = "Connected"; _error = null; _lastWrite = DateTime.UtcNow; _writes++; _registers = registers; _skipped = skipped; }
        }
        public void OnFailure(string error) { lock (_l) { _state = "Error"; _error = error; _failures++; } }
        public ModbusClientStatus Snapshot()
        {
            lock (_l)
                return new ModbusClientStatus
                {
                    Id = Entry.Id, State = _state, Error = _error, LastWriteUtc = _lastWrite,
                    Writes = _writes, Failures = _failures, Registers = _registers, Skipped = _skipped
                };
        }
    }

    public ModbusClientService(IModbusConfigService cfg, ISensorVariableService vars, ICameraVariableService camVars, ILogger<ModbusClientService> logger)
    {
        _cfg = cfg;
        _vars = vars;
        _camVars = camVars;
        _logger = logger;
    }

    public IReadOnlyList<ModbusClientStatus> GetAll()
    {
        List<Worker> copy;
        lock (_lock) copy = _workers.ToList();
        return copy.Select(w => w.Snapshot()).OrderBy(s => s.Id).ToList();
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
            StopAll();
        }
    }

    /// <summary>Dừng các tác vụ cũ và chạy lại theo khai báo hiện tại (chỉ các Client đang bật).</summary>
    private void Apply()
    {
        lock (_lock)
        {
            StopAllLocked();
            if (_stopping.IsCancellationRequested) return;
            foreach (var entry in _cfg.Get().Clients)
            {
                var w = new Worker { Entry = entry };
                _workers.Add(w);
                if (!entry.Enabled) { w.Set("Disabled"); continue; }
                var token = CancellationTokenSource.CreateLinkedTokenSource(w.Cts.Token, _stopping).Token;
                _ = Task.Run(() => RunAsync(w, token));
            }
        }
    }

    private void StopAll() { lock (_lock) StopAllLocked(); }
    private void StopAllLocked()
    {
        foreach (var w in _workers) { try { w.Cts.Cancel(); } catch { /* da huy */ } }
        _workers.Clear();
    }

    private async Task RunAsync(Worker w, CancellationToken ct)
    {
        var e = w.Entry;
        var maps = e.Mappings.OrderBy(m => m.Address).ToList();
        if (maps.Count == 0) { w.Set("Idle", "Chưa gán biến MS nào cho thanh ghi."); return; }

        var names = maps.Select(m => m.Variable).ToList();
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(e.IntervalMs, ModbusConfigService.MinIntervalMs, ModbusConfigService.MaxIntervalMs));
        ModbusTcpConnection? conn = null;
        int fails = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();
                bool ok = false;
                try
                {
                    if (conn is null)
                    {
                        w.Set("Connecting");
                        conn = await ModbusTcpConnection.ConnectAsync(e.Ip, e.Port, ConnectTimeout, ct);
                    }

                    var values = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);     // biến MS + biến MC
                    foreach (var kv in _vars.GetValues(names)) values[kv.Key] = kv.Value;
                    foreach (var kv in _camVars.GetValues(names)) values[kv.Key] = kv.Value;
                    var (blocks, skipped) = BuildBlocks(maps, values);

                    string? deviceError = null;
                    int written = 0;
                    foreach (var (start, regs) in blocks)
                    {
                        try
                        {
                            await conn.WriteMultipleRegistersAsync((byte)e.UnitId, start, regs, IoTimeout, ct);
                            written += regs.Length;
                        }
                        catch (ModbusException mex)             // thiết bị từ chối khối này nhưng kết nối vẫn tốt: thử tiếp khối khác
                        {
                            deviceError = $"Thanh ghi {start}–{start + regs.Length - 1}: {mex.Message}";
                        }
                    }

                    if (deviceError is null) { w.OnWrite(written, skipped); ok = true; fails = 0; }
                    else w.OnFailure(deviceError);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    conn?.Dispose();
                    conn = null;                                // lần sau kết nối lại
                    fails++;
                    w.OnFailure(ex.Message);
                    _logger.LogDebug(ex, "Modbus client {Id} ({Ip}:{Port}) loi.", e.Id, e.Ip, e.Port);
                }

                TimeSpan wait;
                if (ok || conn is not null)
                {
                    wait = interval - Stopwatch.GetElapsedTime(started);
                    if (wait < TimeSpan.FromMilliseconds(10)) wait = TimeSpan.FromMilliseconds(10);
                }
                else
                {
                    // mất kết nối: thử lại thưa dần (không dồn tải khi thiết bị tắt), tối đa 30 giây
                    var backoff = Math.Min(MaxBackoffMs, 1000 * (1 << Math.Min(fails, 5)));
                    wait = TimeSpan.FromMilliseconds(Math.Max(interval.TotalMilliseconds, backoff));
                }
                await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) { /* dung do cau hinh doi / tat chuong trinh */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Modbus client {Id} dung bat thuong.", e.Id);
            w.OnFailure(ex.Message);
        }
        finally { conn?.Dispose(); }
    }

    /// <summary>
    /// Gộp các thanh ghi có địa chỉ liên tiếp thành từng khối (tối đa 123 thanh ghi/khối). Biến chưa có giá trị thì bỏ qua
    /// (không ghi đè dữ liệu cũ trên thiết bị). Giá trị ngoài 0–65535 được cắt về giới hạn của thanh ghi 16 bit.
    /// </summary>
    internal static (List<(ushort Start, ushort[] Registers)> Blocks, int Skipped) BuildBlocks(
        IReadOnlyList<ModbusMapEntry> sortedMaps, IReadOnlyDictionary<string, long?> values)
    {
        var blocks = new List<(ushort, ushort[])>();
        var run = new List<ushort>();
        int runStart = 0, next = -1, skipped = 0;

        void Flush()
        {
            if (run.Count > 0) blocks.Add(((ushort)runStart, run.ToArray()));
            run.Clear();
        }

        foreach (var m in sortedMaps)
        {
            if (!values.TryGetValue(m.Variable, out var v) || v is null) { skipped++; continue; }
            ushort reg = (ushort)Math.Clamp(v.Value, 0, 65535);

            if (run.Count > 0 && (m.Address != next || run.Count >= ModbusTcpConnection.MaxRegistersPerWrite)) Flush();
            if (run.Count == 0) runStart = m.Address;
            run.Add(reg);
            next = m.Address + 1;
        }
        Flush();
        return (blocks, skipped);
    }
}
