using System.Collections.Concurrent;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Sensor;

/// <summary>Ảnh chụp trạng thái cảm biến để trang "Cảm biến" hiển thị.</summary>
public sealed class SensorSnapshot
{
    public string Type { get; set; } = "";
    public int DeviceCount { get; set; }
    public int SensorsPerDevice { get; set; }
    /// <summary>Keytop: server TCP đang lắng nghe.</summary>
    public bool Listening { get; set; }
    /// <summary>MGT200: kết nối tới converter đang tốt.</summary>
    public bool ConverterOk { get; set; }
    public string? Note { get; set; }
    public int Free { get; set; }
    public int Occupied { get; set; }
    /// <summary>Cảm biến lỗi + toàn bộ cảm biến của thiết bị đang mất kết nối.</summary>
    public int Error { get; set; }
    /// <summary>Cảm biến của thiết bị đang kết nối nhưng chưa báo trạng thái lần nào.</summary>
    public int Unknown { get; set; }
    public List<SensorDeviceView> Devices { get; set; } = new();
}

public sealed class SensorDeviceView
{
    /// <summary>Số thứ tự hiển thị (ZCU 1, 2, 3…).</summary>
    public int No { get; set; }
    public bool Enabled { get; set; }
    public bool Online { get; set; }
    public DateTime? LastSeen { get; set; }
    public string? LastFrame { get; set; }
    /// <summary>Mỗi ký tự là 1 cảm biến: '0' trống, '1' có xe, '2' lỗi, '?' chưa có dữ liệu.</summary>
    public string States { get; set; } = "";
    public int Free { get; set; }
    public int Occupied { get; set; }
    public int Error { get; set; }
    public int Unknown { get; set; }
}

/// <summary>Kết quả gửi 1 lệnh tới converter MGT200.</summary>
public sealed class SensorCommandResult
{
    public bool Ok { get; set; }
    /// <summary>Nội dung ZCU trả về (có thể rỗng nếu không phản hồi).</summary>
    public string Reply { get; set; } = "";
    public string? Error { get; set; }
}

/// <summary>Lệnh đang chờ tiến trình nền gửi đi (gửi xen giữa các lượt hỏi để không tranh kết nối với converter).</summary>
public sealed class SensorPendingCommand
{
    public string Frame { get; init; } = "";
    public TaskCompletionSource<SensorCommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Người gọi đã bỏ chờ (hết thời gian) — không gửi nữa.</summary>
    public volatile bool Abandoned;
}

public interface ISensorService
{
    SensorSettings Settings { get; }
    /// <summary>Phát khi cài đặt thật sự thay đổi — tiến trình nền dừng vòng chạy cũ và chạy lại theo cài đặt mới.</summary>
    event Action? SettingsChanged;
    (SensorSettings? Saved, string? Error) SaveSettings(SensorSettings input);
    SensorSnapshot GetSnapshot();

    /// <summary>Gửi 1 lệnh cài đặt/đọc tới converter MGT200 và chờ phản hồi (lệnh được tiến trình nền gửi xen giữa các lượt hỏi ZCU).</summary>
    Task<SensorCommandResult> SubmitPm200CommandAsync(string frame, TimeSpan timeout);

    // ----- phía tiến trình nền (driver) báo về -----
    /// <summary>Lấy lệnh kế tiếp đang chờ gửi, hoặc null nếu không có.</summary>
    SensorPendingCommand? DequeueCommand();
    void SetListening(bool listening, string? note);
    void SetConverterOk(bool ok, string? note);
    void SetDeviceOnline(int device, bool online);
    void ApplyPm200(int device, byte[] states, string rawFrame);
    void NoteInvalidFrame(int device, string rawFrame);
    /// <summary>Keytop gửi dữ liệu tới. Trả về true nếu gói hợp lệ và đã áp dụng.</summary>
    bool ApplyKeytopFrame(string remoteIp, string payload, out string message);
    /// <summary>Keytop nào quá thời gian chờ thì đánh dấu mất kết nối.</summary>
    void ExpireKeytop(DateTime nowUtc);
}

/// <summary>
/// Trạng thái 2 loại cảm biến (MGT200 / Keytop) trong bộ nhớ — tương đương KTB._Sensor_Status_ / _Sensor_Err_ /
/// PM200_Status / KeytopStatus của PGS. Cài đặt lưu data/sensors.json.
/// </summary>
public sealed class SensorService : ISensorService
{
    private const string StateKey = "sensors";
    private const int MaxDevices = SensorSettings.KeytopMax;     // 85 (>= 32 ZCU của MGT200)
    private const int MaxSensors = SensorSettings.Pm200SensorsPerZcu; // 160 (>= 60 của Keytop)

    private readonly IPersistenceService _persistence;
    private readonly object _lock = new();
    private volatile SensorSettings _settings;

    private readonly byte[][] _states = new byte[MaxDevices][];
    private readonly bool[] _online = new bool[MaxDevices];
    private readonly DateTime?[] _lastSeen = new DateTime?[MaxDevices];
    private readonly string?[] _lastFrame = new string?[MaxDevices];
    private readonly ConcurrentQueue<SensorPendingCommand> _commands = new();
    private bool _listening;
    private bool _converterOk;
    private string? _note;

    public event Action? SettingsChanged;

    public SensorService(IPersistenceService persistence)
    {
        _persistence = persistence;
        _settings = (_persistence.Load<SensorSettings>(StateKey) ?? new SensorSettings()).Sanitized();
        for (int d = 0; d < MaxDevices; d++)
        {
            _states[d] = new byte[MaxSensors];
            Array.Fill(_states[d], SensorProtocol.Unknown);
        }
    }

    public SensorSettings Settings => _settings;

    public (SensorSettings? Saved, string? Error) SaveSettings(SensorSettings input)
    {
        var clean = (input ?? new SensorSettings()).Sanitized();
        var error = clean.Validate();
        if (error is not null) return (null, error);

        lock (_lock)
        {
            var cur = _settings;
            if (cur.Stamp() == clean.Stamp()) return (cur, null); // không đổi gì: không ghi đĩa, không khởi động lại kết nối

            // Đổi loại cảm biến hoặc địa chỉ kết nối thì dữ liệu cũ không còn đúng nữa
            if (cur.Type != clean.Type || cur.Pm200Ip != clean.Pm200Ip || cur.Pm200Port != clean.Pm200Port
                || cur.KeytopPort != clean.KeytopPort || cur.KeytopZcu0Ip != clean.KeytopZcu0Ip)
                ClearStatesLocked();

            _settings = clean;
            _persistence.Save(StateKey, clean);
        }

        FailPendingCommands("Cài đặt cảm biến vừa thay đổi, hãy thử lại.");
        SettingsChanged?.Invoke();
        return (clean, null);
    }

    public async Task<SensorCommandResult> SubmitPm200CommandAsync(string frame, TimeSpan timeout)
    {
        if (_settings.Type != SensorType.Pm200)
            return new SensorCommandResult { Error = "Đang không ở chế độ MGT200." };
        if (_commands.Count >= 20)
            return new SensorCommandResult { Error = "Đang có quá nhiều lệnh chờ gửi, hãy thử lại sau." };

        var cmd = new SensorPendingCommand { Frame = frame };
        _commands.Enqueue(cmd);
        try
        {
            return await cmd.Completion.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            cmd.Abandoned = true;
            return new SensorCommandResult { Error = "Hết thời gian chờ: chưa gửi được lệnh (converter không kết nối hoặc đang bận)." };
        }
    }

    public SensorPendingCommand? DequeueCommand() => _commands.TryDequeue(out var c) ? c : null;

    private void FailPendingCommands(string error)
    {
        while (_commands.TryDequeue(out var c))
            c.Completion.TrySetResult(new SensorCommandResult { Error = error });
    }

    private void ClearStatesLocked()
    {
        for (int d = 0; d < MaxDevices; d++)
        {
            Array.Fill(_states[d], SensorProtocol.Unknown);
            _online[d] = false;
            _lastSeen[d] = null;
            _lastFrame[d] = null;
        }
        _listening = false;
        _converterOk = false;
        _note = null;
    }

    // ------------------------------------------------------------------ đọc để hiển thị
    public SensorSnapshot GetSnapshot()
    {
        var s = _settings;
        bool keytop = s.Type == SensorType.Keytop;
        int count = keytop ? s.KeytopCount : SensorSettings.Pm200MaxZcu;
        int per = keytop ? SensorSettings.KeytopSensorsPerDevice : SensorSettings.Pm200SensorsPerZcu;

        var snap = new SensorSnapshot { Type = s.Type.ToString(), DeviceCount = count, SensorsPerDevice = per };
        lock (_lock)
        {
            snap.Listening = _listening;
            snap.ConverterOk = _converterOk;
            snap.Note = _note;

            for (int d = 0; d < count; d++)
            {
                bool enabled = keytop || s.Pm200Enabled[d];
                var st = _states[d];
                var chars = new char[per];
                int free = 0, occ = 0, err = 0, unk = 0;

                for (int k = 0; k < per; k++)
                {
                    byte v = st[k];
                    chars[k] = v switch { 0 => '0', 1 => '1', 2 => '2', _ => '?' };
                    if (!_online[d]) continue;
                    if (v == SensorProtocol.Free) free++;
                    else if (v == SensorProtocol.Occupied) occ++;
                    else if (v == SensorProtocol.Error) err++;
                    else unk++;
                }
                if (!_online[d]) err = per; // mất kết nối: coi mọi cảm biến của thiết bị là lỗi (giống PGS)

                snap.Devices.Add(new SensorDeviceView
                {
                    No = d + 1,
                    Enabled = enabled,
                    Online = _online[d],
                    LastSeen = _lastSeen[d],
                    LastFrame = _lastFrame[d],
                    States = new string(chars),
                    Free = free, Occupied = occ, Error = err, Unknown = unk
                });

                if (enabled)
                {
                    snap.Free += free; snap.Occupied += occ; snap.Error += err; snap.Unknown += unk;
                }
            }
        }
        return snap;
    }

    // ------------------------------------------------------------------ driver báo về
    public void SetListening(bool listening, string? note)
    {
        lock (_lock) { _listening = listening; _note = note; }
    }

    public void SetConverterOk(bool ok, string? note)
    {
        lock (_lock) { _converterOk = ok; _note = ok ? null : note; }
    }

    public void SetDeviceOnline(int device, bool online)
    {
        if (device < 0 || device >= MaxDevices) return;
        lock (_lock) { _online[device] = online; }
    }

    public void ApplyPm200(int device, byte[] states, string rawFrame)
    {
        if (device < 0 || device >= SensorSettings.Pm200MaxZcu) return;
        lock (_lock)
        {
            Array.Copy(states, _states[device], Math.Min(states.Length, MaxSensors));
            _online[device] = true;
            _lastSeen[device] = DateTime.UtcNow;
            _lastFrame[device] = Truncate(rawFrame);
        }
    }

    public void NoteInvalidFrame(int device, string rawFrame)
    {
        if (device < 0 || device >= MaxDevices) return;
        lock (_lock)
        {
            _online[device] = true; // ZCU có trả lời (giống PGS: có dữ liệu về là còn kết nối), nhưng khung sai nên không cập nhật cảm biến
            _lastSeen[device] = DateTime.UtcNow;
            _lastFrame[device] = $"(khung không hợp lệ, {rawFrame.Length} ký tự) " + Truncate(rawFrame);
        }
    }

    public bool ApplyKeytopFrame(string remoteIp, string payload, out string message)
    {
        var s = _settings;
        if (s.Type != SensorType.Keytop)
        {
            message = "Đang không ở chế độ Keytop.";
            return false;
        }

        int device = SensorProtocol.Keytop.DeviceIndexFromIp(remoteIp, s.KeytopZcu0Ip);
        if (device < 0 || device >= s.KeytopCount)
        {
            message = $"IP {remoteIp} không thuộc dải Keytop (ZCU0 = {s.KeytopZcu0Ip}, số lượng = {s.KeytopCount}).";
            return false;
        }

        if (!SensorProtocol.Keytop.TryParse(payload, out var items))
        {
            lock (_lock)
            {
                _online[device] = true;
                _lastSeen[device] = DateTime.UtcNow;
                _lastFrame[device] = $"(gói không hợp lệ) " + Truncate(payload);
            }
            message = "Gói dữ liệu Keytop không hợp lệ.";
            return false;
        }

        lock (_lock)
        {
            foreach (var (sensor, state) in items)
                _states[device][sensor - 1] = state;
            _online[device] = true;
            _lastSeen[device] = DateTime.UtcNow;
            _lastFrame[device] = Truncate(payload);
        }
        message = $"ZCU {device}: cập nhật {items.Count} cảm biến.";
        return true;
    }

    public void ExpireKeytop(DateTime nowUtc)
    {
        var s = _settings;
        if (s.Type != SensorType.Keytop) return;
        var limit = TimeSpan.FromSeconds(s.KeytopTimeoutSeconds);
        lock (_lock)
        {
            for (int d = 0; d < s.KeytopCount; d++)
                if (_online[d] && _lastSeen[d] is DateTime t && nowUtc - t > limit)
                    _online[d] = false; // quá hạn không có dữ liệu: mất kết nối (trạng thái cũ giữ lại để tham khảo)
        }
    }

    private static string Truncate(string? s)
    {
        s ??= "";
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length <= 300 ? s : s.Substring(0, 300) + "…";
    }
}
