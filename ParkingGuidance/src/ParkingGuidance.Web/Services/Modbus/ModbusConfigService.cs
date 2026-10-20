using System.Net;
using System.Net.Sockets;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Camera;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Sensor;

namespace ParkingGuidance.Web.Services.Modbus;

/// <summary>
/// Khai báo Modbus TCP/IP (Server: cổng sẽ mở; Client: IP + cổng thiết bị). Chỉ giữ + kiểm tra + lưu cấu hình, KHÔNG mở kết nối.
/// Phần chạy thật (gateway nền) sẽ đọc cấu hình từ đây và đăng ký <see cref="Changed"/> để áp dụng khi người dùng bấm Lưu.
/// </summary>
public interface IModbusConfigService
{
    ModbusConfig Get();
    /// <summary>Thay toàn bộ danh sách Server. Trả về null nếu thành công, ngược lại là thông báo lỗi (không lưu gì).</summary>
    string? SaveServers(IEnumerable<ModbusServerEntry> servers);
    /// <summary>Thay toàn bộ danh sách Client. Trả về null nếu thành công, ngược lại là thông báo lỗi (không lưu gì).</summary>
    string? SaveClients(IEnumerable<ModbusClientEntry> clients);
    /// <summary>Xảy ra sau khi cấu hình được lưu thành công.</summary>
    event Action? Changed;
}

public sealed class ModbusConfigService : IModbusConfigService
{
    private const string StateKey = "modbus-config";
    public const int MaxEntries = 32;
    public const int MaxMappings = 500;
    public const int MinIntervalMs = 100;
    public const int MaxIntervalMs = 3_600_000;
    private const int MaxDescription = 200;

    private readonly IPersistenceService _persistence;
    private readonly ISensorService _sensors;
    private readonly ISensorVariableService _vars;
    private readonly ICameraVariableService _camVars;
    private readonly ILogger<ModbusConfigService> _logger;
    private readonly object _lock = new();
    private ModbusConfig _cfg = new();

    public event Action? Changed;

    public ModbusConfigService(IPersistenceService persistence, ISensorService sensors, ISensorVariableService vars, ICameraVariableService camVars, ILogger<ModbusConfigService> logger)
    {
        _persistence = persistence;
        _sensors = sensors;
        _vars = vars;
        _camVars = camVars;
        _logger = logger;
        try
        {
            var loaded = _persistence.Load<ModbusConfig>(StateKey);
            if (loaded is not null)
            {
                _cfg.Servers = (loaded.Servers ?? new()).Where(s => s is not null && s.Id >= 0).OrderBy(s => s.Id).ToList();
                _cfg.Clients = (loaded.Clients ?? new()).Where(c => c is not null && c.Id >= 0).OrderBy(c => c.Id).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong doc duoc khai bao Modbus; dung danh sach trong.");
        }
    }

    public ModbusConfig Get()
    {
        lock (_lock)
            return new ModbusConfig
            {
                Servers = _cfg.Servers.Select(s => s.Clone()).ToList(),
                Clients = _cfg.Clients.Select(c => c.Clone()).ToList()
            };
    }

    public string? SaveServers(IEnumerable<ModbusServerEntry> servers)
    {
        var list = (servers ?? Enumerable.Empty<ModbusServerEntry>()).Where(s => s is not null).Select(s => s.Clone()).ToList();
        if (list.Count > MaxEntries) return $"Tối đa {MaxEntries} khai báo Server.";

        var error = CheckIds(list.Select(s => s.Id), "Server");
        if (error is not null) return error;

        var keytopPort = _sensors.Settings.Type.ToString() == "Keytop" ? _sensors.Settings.KeytopPort : (int?)null;
        foreach (var s in list)
        {
            if (s.Port is < 1 or > 65535) return $"Server {s.Id}: cổng phải từ 1 đến 65535.";
            s.Description = (s.Description ?? "").Trim();
            if (s.Description.Length > MaxDescription) return $"Server {s.Id}: mô tả tối đa {MaxDescription} ký tự.";
            if (s.Enabled && keytopPort == s.Port)
                return $"Server {s.Id}: cổng {s.Port} đang được server Keytop (cảm biến) dùng, hãy chọn cổng khác.";
        }

        var dup = list.Where(s => s.Enabled).GroupBy(s => s.Port).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            return $"Cổng {dup.Key} bị khai báo trùng ở các Server {string.Join(", ", dup.Select(s => s.Id))} (đang bật).";

        lock (_lock)
        {
            _cfg.Servers = list.OrderBy(s => s.Id).ToList();
            Persist();
        }
        RaiseChanged();
        return null;
    }

    public string? SaveClients(IEnumerable<ModbusClientEntry> clients)
    {
        var list = (clients ?? Enumerable.Empty<ModbusClientEntry>()).Where(c => c is not null).Select(c => c.Clone()).ToList();
        if (list.Count > MaxEntries) return $"Tối đa {MaxEntries} khai báo Client.";

        var error = CheckIds(list.Select(c => c.Id), "Client");
        if (error is not null) return error;

        foreach (var c in list)
        {
            c.Ip = (c.Ip ?? "").Trim();
            if (!IsIPv4(c.Ip)) return $"Client {c.Id}: địa chỉ IP không hợp lệ (ví dụ 192.168.1.10).";
            if (c.Port is < 1 or > 65535) return $"Client {c.Id}: cổng phải từ 1 đến 65535.";
            if (c.UnitId is < 0 or > 255) return $"Client {c.Id}: Unit ID phải từ 0 đến 255.";
            if (c.IntervalMs < MinIntervalMs || c.IntervalMs > MaxIntervalMs)
                return $"Client {c.Id}: chu kỳ gửi phải từ {MinIntervalMs} ms đến {MaxIntervalMs} ms.";
            c.Description = (c.Description ?? "").Trim();
            if (c.Description.Length > MaxDescription) return $"Client {c.Id}: mô tả tối đa {MaxDescription} ký tự.";

            c.Mappings ??= new();
            if (c.Mappings.Count > MaxMappings) return $"Client {c.Id}: tối đa {MaxMappings} dòng bản đồ thanh ghi.";
            var usedAddr = new HashSet<int>();
            foreach (var m in c.Mappings)
            {
                m.Variable = (m.Variable ?? "").Trim();
                if (m.Variable.Length == 0) return $"Client {c.Id}: có dòng bản đồ chưa chọn biến MS.";
                var knownName = _vars.Get(m.Variable)?.Name ?? _camVars.Get(m.Variable)?.Name;      // biến MS hoặc MC
                if (knownName is null) return $"Client {c.Id}: biến '{m.Variable}' không tồn tại.";
                m.Variable = knownName;                                    // chuẩn hoá hoa/thường theo tên thật
                if (m.Address is < 0 or > 65535) return $"Client {c.Id}: địa chỉ thanh ghi của {m.Variable} phải từ 0 đến 65535.";
                if (!usedAddr.Add(m.Address)) return $"Client {c.Id}: địa chỉ thanh ghi {m.Address} bị gán cho nhiều biến.";
            }
            c.Mappings = c.Mappings.OrderBy(m => m.Address).ToList();
        }

        var dup = list.Where(c => c.Enabled).GroupBy(c => (c.Ip, c.Port, c.UnitId)).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            return $"Thiết bị {dup.Key.Ip}:{dup.Key.Port} (Unit {dup.Key.UnitId}) bị khai báo trùng ở các Client {string.Join(", ", dup.Select(c => c.Id))} (đang bật).";

        lock (_lock)
        {
            _cfg.Clients = list.OrderBy(c => c.Id).ToList();
            Persist();
        }
        RaiseChanged();
        return null;
    }

    private static string? CheckIds(IEnumerable<int> ids, string kind)
    {
        var seen = new HashSet<int>();
        foreach (var id in ids)
        {
            if (id < 0) return $"{kind}: số thứ tự phải từ 0 trở lên.";
            if (!seen.Add(id)) return $"{kind}: số thứ tự {id} bị trùng.";
        }
        return null;
    }

    private static bool IsIPv4(string s) =>
        s.Count(ch => ch == '.') == 3 && IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    private void Persist()
    {
        try { _persistence.Save(StateKey, _cfg); }
        catch (Exception ex) { _logger.LogError(ex, "Khong luu duoc khai bao Modbus."); }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "Loi khi ap dung khai bao Modbus moi."); }
    }
}
