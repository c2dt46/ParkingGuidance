using System.Text.RegularExpressions;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Tags;

namespace ParkingGuidance.Web.Services.Sensor;

/// <summary>1 biến MS kèm giá trị tại thời điểm đọc (không kèm phần chọn chi tiết — lấy bằng <see cref="ISensorVariableService.Get"/>).</summary>
public sealed class SensorVariableView
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public SensorCountState? Count { get; init; }
    public string Source { get; init; } = "";
    /// <summary>Số cảm biến được chỉ định.</summary>
    public int Selected { get; init; }
    /// <summary>Số cảm biến đang ở trạng thái cần đếm. null = chưa tính được (biến thuộc loại cảm biến khác với loại đang dùng).</summary>
    public int? Value { get; init; }
    /// <summary>Số cảm biến được chỉ định nhưng chưa có dữ liệu (thiết bị đang kết nối nhưng chưa báo lần nào).</summary>
    public int Unknown { get; init; }
    /// <summary>Số cảm biến được chỉ định nằm trên thiết bị không kích hoạt / ngoài số thiết bị đang dùng (không được quét).</summary>
    public int Inactive { get; init; }
    /// <summary>false = biến thuộc loại cảm biến khác với loại đang dùng.</summary>
    public bool Valid { get; init; }
    /// <summary>Tóm tắt: "ZCU 0 (160) · ZCU 3 (32)".</summary>
    public string Devices { get; init; } = "";
}

public interface ISensorVariableService
{
    IReadOnlyList<SensorVariableView> GetAll();
    /// <summary>Bản đầy đủ (kèm phần chọn cảm biến) để sửa.</summary>
    SensorVariable? Get(string name);
    /// <summary>Thêm ngay 1 biến trống (chưa chọn trạng thái, chưa chọn cảm biến) với tên MSn, n nhỏ nhất còn trống bắt đầu từ 0.</summary>
    (SensorVariable? Variable, string? Error) Add();
    /// <summary>Sửa biến (originalName = tên hiện tại; tên không đổi được). Trạng thái đếm đã xác định thì không đổi được.</summary>
    (SensorVariable? Variable, string? Error) Save(string? originalName, SensorVariable dto);
    bool Delete(string name);
    /// <summary>Đọc giá trị 1 biến theo tên — dành cho nơi dùng sau này (Tags, Modbus…). false nếu không có biến hoặc chưa tính được.</summary>
    bool TryGetValue(string name, out long value);
    /// <summary>
    /// Giá trị nhiều biến cùng lúc (1 ảnh chụp trạng thái cảm biến cho tất cả) — dành cho nơi đọc định kỳ như Modbus.
    /// Khoá = tên biến (không phân biệt hoa/thường); biến không tồn tại thì không có trong kết quả; giá trị null = chưa tính được.
    /// </summary>
    IReadOnlyDictionary<string, long?> GetValues(IEnumerable<string> names);
}

/// <summary>
/// Biến MS: đếm 1 trạng thái trên tập cảm biến được chỉ định, tính TRỰC TIẾP từ trạng thái cảm biến thật mỗi lần đọc
/// (không giữ bản sao, không có luồng nền). Quy ước giống phần "Giám sát": thiết bị đang kết nối nhưng mất kết nối thì mọi cảm biến
/// của nó tính là Lỗi; cảm biến chưa báo lần nào không thuộc trạng thái nào.
/// </summary>
public sealed class SensorVariableService : ISensorVariableService
{
    private const string StateKey = "sensor-variables";
    public const int MaxVariables = 200;
    private const int MaxDescription = 200;
    private static readonly Regex NameRx = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

    private readonly IPersistenceService _persistence;
    private readonly ISensorService _sensors;
    private readonly IProjectTagService _projectTags;
    private readonly ILogger<SensorVariableService> _logger;
    private readonly object _lock = new();
    private readonly List<SensorVariable> _vars = new();

    public SensorVariableService(IPersistenceService persistence, ISensorService sensors,
        IProjectTagService projectTags, ILogger<SensorVariableService> logger)
    {
        _persistence = persistence;
        _sensors = sensors;
        _projectTags = projectTags;
        _logger = logger;

        try
        {
            foreach (var v in _persistence.Load<List<SensorVariable>>(StateKey) ?? new())
            {
                if (v is null || !NameRx.IsMatch(v.Name ?? "")) continue;
                if (_vars.Any(x => x.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase))) continue;
                var (clean, _) = CleanBody(v);
                if (clean is null) continue;
                clean.Name = v.Name;
                _vars.Add(clean);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong doc duoc bien MS; dung danh sach trong.");
        }
    }

    // ------------------------------------------------------------------ đọc
    public IReadOnlyList<SensorVariableView> GetAll()
    {
        List<SensorVariable> copy;
        lock (_lock) copy = _vars.Select(v => v.Clone()).ToList();
        if (copy.Count == 0) return new List<SensorVariableView>();

        var snap = _sensors.GetSnapshot();                       // 1 ảnh chụp cho mọi biến
        var current = _sensors.Settings.Type.ToString();
        return copy.OrderBy(SortKey).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                   .Select(v => Evaluate(v, snap, current)).ToList();
    }

    public SensorVariable? Get(string name)
    {
        lock (_lock) return Find(name)?.Clone();
    }

    public bool TryGetValue(string name, out long value)
    {
        value = 0;
        var v = Get(name);
        if (v is null) return false;
        var view = Evaluate(v, _sensors.GetSnapshot(), _sensors.Settings.Type.ToString());
        if (view.Value is null) return false;
        value = view.Value.Value;
        return true;
    }

    public IReadOnlyDictionary<string, long?> GetValues(IEnumerable<string> names)
    {
        var result = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        List<SensorVariable> found = new();
        lock (_lock)
            foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
                if (Find(n) is { } v) found.Add(v.Clone());
        if (found.Count == 0) return result;

        var snap = _sensors.GetSnapshot();
        var current = _sensors.Settings.Type.ToString();
        foreach (var v in found)
        {
            var view = Evaluate(v, snap, current);
            result[v.Name] = view.Value is int i ? i : null;
        }
        return result;
    }

    // ------------------------------------------------------------------ ghi
    public (SensorVariable? Variable, string? Error) Add()
    {
        lock (_lock)
        {
            if (_vars.Count >= MaxVariables) return (null, $"Tối đa {MaxVariables} biến MS.");
            var v = new SensorVariable
            {
                Name = NextAutoName(),
                Count = null,
                Source = _sensors.Settings.Type.ToString(),
                Selection = new()
            };
            _vars.Add(v);
            Persist();
            return (v.Clone(), null);
        }
    }

    public (SensorVariable? Variable, string? Error) Save(string? originalName, SensorVariable dto)
    {
        lock (_lock)
        {
            var cur = Find(originalName);
            if (cur is null) return (null, $"Không tìm thấy biến '{originalName}'.");

            // Mỗi biến chỉ có 1 trạng thái đếm: đã xác định rồi thì không cho đổi.
            if (cur.Count is not null && dto.Count is not null && dto.Count != cur.Count)
                return (null, $"Biến {cur.Name} đã xác định trạng thái đếm, không thể đổi. Muốn đếm trạng thái khác hãy xoá biến và thêm lại.");

            var (clean, error) = CleanBody(dto, cur.Source);
            if (clean is null) return (null, error);

            clean.Name = cur.Name;                           // tên tự đặt, không đổi
            clean.Count = cur.Count ?? dto.Count;            // đã xác định thì giữ nguyên
            _vars[_vars.IndexOf(cur)] = clean;
            Persist();
            return (clean.Clone(), null);
        }
    }

    public bool Delete(string name)
    {
        lock (_lock)
        {
            var cur = Find(name);
            if (cur is null) return false;
            _vars.Remove(cur);
            Persist();
            return true;
        }
    }

    // ------------------------------------------------------------------ nội bộ
    private SensorVariable? Find(string? name) =>
        string.IsNullOrEmpty(name) ? null : _vars.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>MS0, MS1, MS2… — lấy chỉ số nhỏ nhất còn trống (xoá biến giữa chừng thì lần thêm sau lấp lại chỗ trống).</summary>
    private string NextAutoName()
    {
        for (int n = 0; ; n++)
        {
            var cand = "MS" + n;
            if (!_vars.Any(v => v.Name.Equals(cand, StringComparison.OrdinalIgnoreCase))
                && !SystemTagService.IsReserved(cand)
                && _projectTags.Get(cand) is null) return cand;
        }
    }

    /// <summary>Khoá sắp xếp: MS0, MS1, MS2… theo số; tên khác (biến cũ đặt tay) xếp sau.</summary>
    private static int SortKey(SensorVariable v) =>
        v.Name.Length > 2 && v.Name.StartsWith("MS", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(v.Name.AsSpan(2), out var n) && n >= 0 ? n : int.MaxValue;

    private void Persist()
    {
        try { _persistence.Save(StateKey, _vars); }
        catch (Exception ex) { _logger.LogError(ex, "Khong luu duoc bien MS."); }
    }

    private static int DeviceLimit(string source) =>
        source == "Keytop" ? SensorSettings.KeytopMax : SensorSettings.Pm200MaxZcu;

    private static int PerDevice(string source) =>
        source == "Keytop" ? SensorSettings.KeytopSensorsPerDevice : SensorSettings.Pm200SensorsPerZcu;

    /// <summary>Kiểm tra + chuẩn hoá phần thân (mô tả, trạng thái đếm, loại cảm biến, phần chọn); chưa xử lý tên. Gộp các dòng trùng thiết bị.</summary>
    private static (SensorVariable? Clean, string? Error) CleanBody(SensorVariable dto, string? defaultSource = null)
    {
        var source = dto.Source?.Trim();
        if (string.IsNullOrEmpty(source)) source = defaultSource ?? "Pm200";
        source = string.Equals(source, "Keytop", StringComparison.OrdinalIgnoreCase) ? "Keytop" : "Pm200";

        if (dto.Count is { } cnt && !Enum.IsDefined(cnt)) return (null, "Trạng thái cần đếm không hợp lệ.");
        var desc = (dto.Description ?? "").Trim();
        if (desc.Length > MaxDescription) return (null, $"Mô tả tối đa {MaxDescription} ký tự.");

        int per = PerDevice(source), maxDev = DeviceLimit(source);
        var merged = new SortedDictionary<int, char[]>();
        foreach (var p in dto.Selection ?? new List<SensorPick>())
        {
            if (p is null) continue;
            if (p.Device < 0 || p.Device >= maxDev) return (null, $"Thiết bị {p.Device} ngoài phạm vi (0–{maxDev - 1}).");
            var mask = p.Mask ?? "";
            if (mask.Length != per) return (null, $"Phần chọn của thiết bị {p.Device} phải dài đúng {per} ký tự.");
            if (mask.Any(c => c != '0' && c != '1')) return (null, "Phần chọn chỉ gồm ký tự 0 và 1.");

            if (!merged.TryGetValue(p.Device, out var acc)) merged[p.Device] = acc = new string('0', per).ToCharArray();
            for (int k = 0; k < per; k++) if (mask[k] == '1') acc[k] = '1';
        }

        var selection = merged
            .Where(kv => kv.Value.Contains('1'))
            .Select(kv => new SensorPick { Device = kv.Key, Mask = new string(kv.Value) })
            .ToList();

        return (new SensorVariable { Description = desc, Count = dto.Count, Source = source, Selection = selection }, null);
    }

    private static SensorVariableView Evaluate(SensorVariable v, SensorSnapshot snap, string currentSource)
    {
        bool valid = v.Source == currentSource;
        int per = PerDevice(v.Source);
        int selected = 0, counted = 0, unknown = 0, inactive = 0;

        foreach (var pick in v.Selection)
        {
            for (int k = 0; k < pick.Mask.Length && k < per; k++)
            {
                if (pick.Mask[k] != '1') continue;
                selected++;
                if (!valid || v.Count is null) continue;

                if (pick.Device >= snap.Devices.Count) { inactive++; continue; }
                var d = snap.Devices[pick.Device];
                if (!d.Enabled) { inactive++; continue; }
                if (!d.Online)                                   // mất kết nối: mọi cảm biến của thiết bị tính là Lỗi (giống phần Giám sát)
                {
                    if (v.Count == SensorCountState.Error) counted++;
                    continue;
                }

                char c = k < d.States.Length ? d.States[k] : '?';
                if (c == '?') { unknown++; continue; }
                if ((c == '0' && v.Count == SensorCountState.Free)
                    || (c == '1' && v.Count == SensorCountState.Occupied)
                    || (c == '2' && v.Count == SensorCountState.Error))
                    counted++;
            }
        }

        var parts = v.Selection.Select(p => $"ZCU {p.Device} ({p.Mask.Count(c => c == '1')})").ToList();
        var devices = parts.Count <= 6
            ? string.Join(" · ", parts)
            : string.Join(" · ", parts.Take(6)) + $" · … +{parts.Count - 6} ZCU";

        return new SensorVariableView
        {
            Name = v.Name,
            Description = v.Description,
            Count = v.Count,
            Source = v.Source,
            Selected = selected,
            Value = valid && v.Count is not null ? counted : null,
            Unknown = unknown,
            Inactive = inactive,
            Valid = valid,
            Devices = devices
        };
    }
}
