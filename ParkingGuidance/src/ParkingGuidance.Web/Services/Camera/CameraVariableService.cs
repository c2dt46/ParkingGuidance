using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.CameraRegistry;
using ParkingGuidance.Web.Services.Floor;
using ParkingGuidance.Web.Services.Parking;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Tags;

namespace ParkingGuidance.Web.Services.Camera;

/// <summary>1 biến MC kèm giá trị tại thời điểm đọc (phần chọn chi tiết lấy bằng <see cref="ICameraVariableService.Get"/>).</summary>
public sealed class CameraVariableView
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public SensorCountState? Count { get; init; }
    /// <summary>Số vị trí được chỉ định.</summary>
    public int Selected { get; init; }
    /// <summary>Số vị trí đang ở trạng thái cần đếm. null = chưa chọn trạng thái đếm.</summary>
    public int? Value { get; init; }
    /// <summary>Số vị trí được chỉ định nhưng vùng nhận diện đã bị xoá khỏi camera (không được đếm).</summary>
    public int Missing { get; init; }
    /// <summary>Tóm tắt: "B1 / Zone 2 (5) · B1 / Zone 3 (3)".</summary>
    public string Summary { get; init; } = "";
}

/// <summary>1 vị trí đỗ (vùng nhận diện) của camera, để người dùng chọn.</summary>
public sealed class CameraCatalogPosition
{
    public string Key { get; init; } = "";
    public string Number { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>1 camera kèm các vị trí đỗ đã khoanh vùng.</summary>
public sealed class CameraCatalogItem
{
    public string CameraId { get; init; } = "";
    public string Label { get; init; } = "";
    /// <summary>Khoá tầng đã chuẩn hoá (dùng để lọc / ghép khoá vị trí).</summary>
    public string Floor { get; init; } = "";
    /// <summary>Tên tầng để hiển thị (tên mặt bằng, vd "Tầng hầm B1"; không có thì dùng mã tầng gốc).</summary>
    public string FloorName { get; init; } = "";
    public string Zone { get; init; } = "";
    public List<CameraCatalogPosition> Positions { get; init; } = new();
}

/// <summary>1 tầng hầm (mặt bằng) để lọc camera.</summary>
public sealed class CameraCatalogFloor
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
}

/// <summary>Danh mục để chọn vị trí: mọi tầng hầm + mọi camera (kể cả camera chưa khoanh vùng vị trí đỗ nào).</summary>
public sealed class CameraCatalog
{
    public List<CameraCatalogFloor> Floors { get; init; } = new();
    public List<CameraCatalogItem> Cameras { get; init; } = new();
}

public interface ICameraVariableService
{
    IReadOnlyList<CameraVariableView> GetAll();
    CameraVariable? Get(string name);
    /// <summary>Thêm ngay 1 biến trống (chưa chọn trạng thái, chưa chọn vị trí) với tên MCn, n nhỏ nhất còn trống bắt đầu từ 0.</summary>
    (CameraVariable? Variable, string? Error) Add();
    /// <summary>Sửa biến (tên không đổi được). Trạng thái đếm đã xác định thì không đổi được.</summary>
    (CameraVariable? Variable, string? Error) Save(string? originalName, CameraVariable dto);
    bool Delete(string name);
    /// <summary>Giá trị nhiều biến cùng lúc (cho Modbus…). Biến không tồn tại thì không có trong kết quả; null = chưa tính được.</summary>
    IReadOnlyDictionary<string, long?> GetValues(IEnumerable<string> names);
    /// <summary>Danh mục tầng hầm + camera + vị trí đỗ đã khoanh vùng (để chọn trong giao diện).</summary>
    CameraCatalog GetCatalog();
}

/// <summary>
/// Biến MC: đếm 1 trạng thái trên tập vị trí đỗ (vùng nhận diện của camera) được chỉ định, tính TRỰC TIẾP từ trạng thái ô đỗ mỗi lần đọc
/// (không giữ bản sao, không có luồng nền). Còn trống = ô "Empty", Đã đỗ = "Occupied", Lỗi = "Error" (vd camera mất kết nối).
/// Vị trí mà vùng nhận diện đã bị xoá khỏi camera thì không được đếm.
/// </summary>
public sealed class CameraVariableService : ICameraVariableService
{
    private const string StateKey = "camera-variables";
    public const int MaxVariables = 200;
    private const int MaxSlots = 5000;
    private const int MaxDescription = 200;

    private readonly IPersistenceService _persistence;
    private readonly ICameraRegistryService _cameras;
    private readonly IParkingService _parking;
    private readonly IFloorService _floors;
    private readonly IProjectTagService _projectTags;
    private readonly ILogger<CameraVariableService> _logger;
    private readonly object _lock = new();
    private readonly List<CameraVariable> _vars = new();

    public CameraVariableService(IPersistenceService persistence, ICameraRegistryService cameras, IParkingService parking,
        IFloorService floors, IProjectTagService projectTags, ILogger<CameraVariableService> logger)
    {
        _persistence = persistence;
        _cameras = cameras;
        _parking = parking;
        _floors = floors;
        _projectTags = projectTags;
        _logger = logger;

        try
        {
            foreach (var v in _persistence.Load<List<CameraVariable>>(StateKey) ?? new())
            {
                if (v is null || string.IsNullOrWhiteSpace(v.Name)) continue;
                if (_vars.Any(x => x.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase))) continue;
                if (v.Count is { } c && !Enum.IsDefined(c)) v.Count = null;
                v.Slots = (v.Slots ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList();
                v.Description = (v.Description ?? "").Trim();
                _vars.Add(v);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong doc duoc bien MC; dung danh sach trong.");
        }

        // Xoá vị trí đỗ (vùng nhận diện) / xoá camera / đổi tầng-Zone của camera thì bỏ luôn các vị trí đó khỏi dữ liệu biến MC.
        _cameras.Changed += Prune;
        if (_cameras.GetAll().Count > 0) Prune();               // dọn phần còn sót từ trước (không dọn khi chưa có camera nào, tránh xoá nhầm)
    }

    /// <summary>Bỏ khỏi mọi biến MC các vị trí không còn tồn tại trên camera nào, rồi lưu nếu có thay đổi.</summary>
    private void Prune()
    {
        try
        {
            var valid = ExistingKeys();
            lock (_lock)
            {
                bool changed = false;
                foreach (var v in _vars)
                    if (v.Slots.RemoveAll(s => !valid.Contains(s)) > 0) changed = true;
                if (changed) Persist();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong don duoc vi tri da xoa khoi bien MC.");
        }
    }

    // ------------------------------------------------------------------ đọc
    public IReadOnlyList<CameraVariableView> GetAll()
    {
        List<CameraVariable> copy;
        lock (_lock) copy = _vars.Select(v => v.Clone()).ToList();
        if (copy.Count == 0) return new List<CameraVariableView>();

        var valid = ExistingKeys();                              // 1 lần cho mọi biến
        var names = FloorNames();
        return copy.OrderBy(SortKey).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                   .Select(v => Evaluate(v, valid, names)).ToList();
    }

    public CameraVariable? Get(string name)
    {
        lock (_lock) return Find(name)?.Clone();
    }

    public IReadOnlyDictionary<string, long?> GetValues(IEnumerable<string> names)
    {
        var result = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        List<CameraVariable> found = new();
        lock (_lock)
            foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
                if (Find(n) is { } v) found.Add(v.Clone());
        if (found.Count == 0) return result;

        var valid = ExistingKeys();
        foreach (var v in found) result[v.Name] = Evaluate(v, valid, null).Value;
        return result;
    }

    /// <summary>Tên hiển thị của từng tầng theo khoá đã chuẩn hoá: tên mặt bằng nếu có, không thì mã tầng gốc ghi trên camera.</summary>
    private Dictionary<string, string> FloorNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cam in _cameras.GetAll())
        {
            var key = SlotKey.NormalizeFloor(cam.Floor);
            if (!map.ContainsKey(key)) map[key] = string.IsNullOrWhiteSpace(cam.Floor) ? key : cam.Floor.Trim();
        }
        foreach (var plan in _floors.GetFloors())                // tên mặt bằng ưu tiên hơn
        {
            var key = SlotKey.NormalizeFloor(plan.Id);
            map[key] = string.IsNullOrWhiteSpace(plan.Name) ? plan.Id : plan.Name.Trim();
        }
        return map;
    }

    public CameraCatalog GetCatalog()
    {
        var names = FloorNames();
        var cameras = new List<CameraCatalogItem>();
        foreach (var cam in _cameras.GetAll())
        {
            var seen = new HashSet<string>();
            var positions = new List<CameraCatalogPosition>();
            foreach (var z in cam.Zones)
            {
                var key = SlotKey.ForZone(cam.Floor, cam.Zone, z.SlotId);
                if (key is null || !seen.Add(key)) continue;
                positions.Add(new CameraCatalogPosition { Key = key, Number = SlotKey.NumberOfKey(key) ?? "", Name = z.Name ?? "" });
            }
            // camera đã vào bãi đỗ nhưng chưa khoanh vùng vẫn hiện (0 vị trí) để không bị "mất" khỏi danh sách
            if (positions.Count == 0 && !cam.IsCommitted) continue;

            positions = positions.OrderBy(p => int.TryParse(p.Number, out var n) ? n : int.MaxValue).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
            var model = string.IsNullOrWhiteSpace(cam.Model) ? cam.Brand : $"{cam.Brand} {cam.Model}";
            var floorKey = SlotKey.NormalizeFloor(cam.Floor);
            cameras.Add(new CameraCatalogItem
            {
                CameraId = cam.Id,
                Label = $"{cam.IpAddress}" + (string.IsNullOrWhiteSpace(model) ? "" : $" ({model.Trim()})"),
                Floor = floorKey,
                FloorName = names.TryGetValue(floorKey, out var fn) ? fn : floorKey,
                Zone = SlotKey.NormalizeZone(cam.Zone),
                Positions = positions
            });
        }

        // mọi tầng hầm: các mặt bằng đã khai báo + tầng ghi trên camera
        var floors = names.Select(kv => new CameraCatalogFloor { Key = kv.Key, Name = kv.Value })
                          .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();

        return new CameraCatalog
        {
            Floors = floors,
            Cameras = cameras.OrderBy(c => c.Floor, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(c => int.TryParse(c.Zone, out var z) ? z : int.MaxValue)
                             .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    // ------------------------------------------------------------------ ghi
    public (CameraVariable? Variable, string? Error) Add()
    {
        lock (_lock)
        {
            if (_vars.Count >= MaxVariables) return (null, $"Tối đa {MaxVariables} biến MC.");
            var v = new CameraVariable { Name = NextAutoName(), Count = null, Slots = new() };
            _vars.Add(v);
            Persist();
            return (v.Clone(), null);
        }
    }

    public (CameraVariable? Variable, string? Error) Save(string? originalName, CameraVariable dto)
    {
        lock (_lock)
        {
            var cur = Find(originalName);
            if (cur is null) return (null, $"Không tìm thấy biến '{originalName}'.");

            // Mỗi biến chỉ có 1 trạng thái đếm: đã xác định rồi thì không cho đổi.
            if (cur.Count is not null && dto.Count is not null && dto.Count != cur.Count)
                return (null, $"Biến {cur.Name} đã xác định trạng thái đếm, không thể đổi. Muốn đếm trạng thái khác hãy xoá biến và thêm lại.");
            if (dto.Count is { } cnt && !Enum.IsDefined(cnt)) return (null, "Trạng thái cần đếm không hợp lệ.");

            var desc = (dto.Description ?? "").Trim();
            if (desc.Length > MaxDescription) return (null, $"Mô tả tối đa {MaxDescription} ký tự.");

            var raw = (dto.Slots ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList();
            if (raw.Count > MaxSlots) return (null, $"Tối đa {MaxSlots} vị trí cho 1 biến.");
            foreach (var s in raw)
                if (!SlotKey.TryParse(s, out _, out _, out _)) return (null, $"Vị trí '{s}' không hợp lệ.");

            // chỉ giữ vị trí còn tồn tại (vùng nhận diện chưa bị xoá khỏi camera)
            var valid = ExistingKeys();
            var slots = raw.Where(valid.Contains).ToList();

            var clean = new CameraVariable { Name = cur.Name, Description = desc, Count = cur.Count ?? dto.Count, Slots = slots };
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
    private CameraVariable? Find(string? name) =>
        string.IsNullOrEmpty(name) ? null : _vars.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>MC0, MC1, MC2… — lấy chỉ số nhỏ nhất còn trống (xoá biến giữa chừng thì lần thêm sau lấp lại chỗ trống).</summary>
    private string NextAutoName()
    {
        for (int n = 0; ; n++)
        {
            var cand = "MC" + n;
            if (!_vars.Any(v => v.Name.Equals(cand, StringComparison.OrdinalIgnoreCase))
                && !SystemTagService.IsReserved(cand)
                && _projectTags.Get(cand) is null) return cand;
        }
    }

    /// <summary>Khoá sắp xếp: MC0, MC1, MC2… theo số; tên khác xếp sau.</summary>
    private static int SortKey(CameraVariable v) =>
        v.Name.Length > 2 && v.Name.StartsWith("MC", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(v.Name.AsSpan(2), out var n) && n >= 0 ? n : int.MaxValue;

    private void Persist()
    {
        try { _persistence.Save(StateKey, _vars); }
        catch (Exception ex) { _logger.LogError(ex, "Khong luu duoc bien MC."); }
    }

    /// <summary>Khoá ô của mọi vùng nhận diện hiện có trên các camera.</summary>
    private HashSet<string> ExistingKeys()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cam in _cameras.GetAll())
            foreach (var z in cam.Zones)
                if (SlotKey.ForZone(cam.Floor, cam.Zone, z.SlotId) is { } k) set.Add(k);
        return set;
    }

    private CameraVariableView Evaluate(CameraVariable v, HashSet<string> valid, Dictionary<string, string>? floorNames)
    {
        int selected = 0, counted = 0, missing = 0;
        var groups = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var target = v.Count switch
        {
            SensorCountState.Free => (SlotStatus?)SlotStatus.Empty,
            SensorCountState.Occupied => SlotStatus.Occupied,
            SensorCountState.Error => SlotStatus.Error,
            _ => null
        };

        foreach (var key in v.Slots)
        {
            selected++;
            if (!valid.Contains(key)) { missing++; continue; }

            if (SlotKey.TryParse(key, out var floor, out var zone, out _))
            {
                var g = $"{(floorNames is not null && floorNames.TryGetValue(floor, out var fn) ? fn : floor)} / Zone {zone}";
                groups[g] = (groups.TryGetValue(g, out var prev) ? prev : 0) + 1;
            }
            if (target is null) continue;

            var status = _parking.FindSlot(key)?.Status ?? SlotStatus.Empty;     // ô chưa có dữ liệu = còn trống
            if (status == target) counted++;
        }

        var parts = groups.Select(kv => $"{kv.Key} ({kv.Value})").ToList();
        var summary = parts.Count <= 6
            ? string.Join(" · ", parts)
            : string.Join(" · ", parts.Take(6)) + $" · … +{parts.Count - 6} khu";

        return new CameraVariableView
        {
            Name = v.Name, Description = v.Description, Count = v.Count, Selected = selected,
            Value = target is null ? null : counted, Missing = missing, Summary = summary
        };
    }
}
