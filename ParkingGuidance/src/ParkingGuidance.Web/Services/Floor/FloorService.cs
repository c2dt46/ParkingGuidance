using ParkingGuidance.Web.Services.Project;
using System.Collections.Concurrent;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Floor;

public interface IFloorService
{
    IReadOnlyList<FloorPlan> GetFloors();
    FloorPlan AddFloor(string id, string name);
    FloorPlan? RenameFloor(string id, string name);
    FloorPlan? SetBackgroundImage(string id, string? url);
    /// <summary>Đặt mặt bằng khởi động (chỉ 1 mặt bằng duy nhất; các mặt bằng khác tự bỏ đánh dấu).</summary>
    FloorPlan? SetStartup(string id);
    bool DeleteFloor(string id);
    /// <summary>Cấu hình màn hình Header / Navigation (luôn có sẵn): dùng / không dùng + kích thước pixel + toàn kích thước + (Navigation) tài khoản.</summary>
    (FloorPlan? Floor, string? Error) UpdatePanel(string id, bool? enabled, int? width, int? height, bool? showAccount = null, bool? fullSize = null);
    /// <summary>Đặt màu nền (#RRGGBB) cho mặt bằng bất kỳ (kể cả Header / Navigation); null / rỗng = màu mặc định.</summary>
    FloorPlan? SetBackgroundColor(string id, string? color);
}

/// <summary>Lưu trữ danh sách mặt bằng — tự tải lại khi khởi động và tự lưu sau mỗi thay đổi
/// qua IPersistenceService (file data/floors.json), không mất khi restart server.</summary>
public class FloorService : IFloorService, IProjectScoped
{
    private readonly ConcurrentDictionary<string, FloorPlan> _floors = new();
    private readonly IPersistenceService _persistence;
    private const string StateKey = "floors";
    private readonly object _gate = new();

    /// <summary>Mặt bằng tự tạo khi danh sách trống (người dùng đã xoá hết mặt bằng). Mã viết hoa, không có dấu "-"
    /// vì mã tầng nằm trong khoá ô đỗ "{TẦNG}-{ZONE}-{SỐ}".</summary>
    public const string DefaultFloorId = "DEFAULT";
    public const string DefaultFloorName = "Mặt bằng mặc định";

    public FloorService(IPersistenceService persistence)
    {
        _persistence = persistence;
        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        var saved = _persistence.Load<List<FloorPlan>>(StateKey);
        // Xoá + nạp lại trong CÙNG MỘT lần khoá: nếu để hở giữa 2 bước, 1 request đọc danh sách đúng lúc đó thấy "trống" và
        // GetFloors() sẽ tự tạo thêm mặt bằng "DEFAULT" vào dự án vừa mở.
        lock (_gate)
        {
            _floors.Clear();
            if (saved is not null && saved.Count > 0)
            {
                foreach (var f in saved) _floors[f.Id] = f;
            }
            else
            {
                _floors["B1"] = new FloorPlan { Id = "B1", Name = "Tầng hầm B1", IsStartup = true };
                SaveState();
            }
            EnsureDefaults();
            NormalizeStartup();
        }
    }

    // ---- IProjectScoped: lưu chung / mở dự án khác ----
    public string ProjectPartName => "Mặt bằng";
    public bool FlushProject() { lock (_gate) return _persistence.Commit(StateKey, _floors.Values.ToList()); }
    public void ReloadProject() => LoadFromDisk();

    private void SaveState() => _persistence.Save(StateKey, _floors.Values.ToList());

    public IReadOnlyList<FloorPlan> GetFloors()
    {
        lock (_gate)
        {
            EnsureAtLeastOneFloor();
            EnsureDefaults();
            NormalizeStartup();
        }
        return _floors.Values.OrderBy(f => f.Id).ToList();
    }

    /// <summary>Bảo đảm đúng 1 mặt bằng khởi động: chưa có (dữ liệu cũ / mặt bằng khởi động vừa bị xoá) thì lấy mặt bằng đầu danh sách;
    /// có nhiều hơn 1 (dữ liệu hỏng) thì chỉ giữ cái đầu tiên. Gọi trong lock(_gate).</summary>
    private void NormalizeStartup()
    {
        if (_floors.IsEmpty) return;
        var ordered = _floors.Values.OrderBy(f => f.Id).ToList();
        var normals = ordered.Where(f => !f.IsPanel).ToList();   // Header / Navigation không bao giờ là mặt bằng khởi động
        var keep = normals.FirstOrDefault(f => f.IsStartup) ?? normals.FirstOrDefault();
        bool changed = false;
        foreach (var f in ordered)
        {
            var want = keep is not null && ReferenceEquals(f, keep);
            if (f.IsStartup != want) { f.IsStartup = want; changed = true; }
        }
        if (changed) SaveState();
    }

    public FloorPlan? SetStartup(string id)
    {
        lock (_gate)
        {
            if (!_floors.TryGetValue(id, out var target) || target.IsPanel) return null;
            foreach (var f in _floors.Values) f.IsStartup = ReferenceEquals(f, target);
            SaveState();
            return target;
        }
    }

    /// <summary>Luôn có ít nhất 1 mặt bằng: nếu danh sách trống thì tự tạo "Mặt bằng mặc định". Gọi trong lock(_gate).</summary>
    private void EnsureAtLeastOneFloor()
    {
        if (_floors.Values.Any(f => !f.IsPanel)) return;   // phải còn ít nhất 1 mặt bằng đỗ xe (Header/Navigation không tính)
        _floors[DefaultFloorId] = new FloorPlan { Id = DefaultFloorId, Name = DefaultFloorName, IsStartup = true };
        SaveState();
    }

    /// <summary>Mọi dự án luôn có đúng 1 Header + 1 Navigation (mặc định chưa dùng), tên cố định. Dữ liệu từ bản trước (đã tạo bằng tay) được giữ nguyên
    /// mã và coi là đang dùng. Gọi trong lock(_gate).</summary>
    private void EnsureDefaults()
    {
        bool changed = false;
        foreach (var kind in new[] { FloorKind.Header, FloorKind.Navigation })
        {
            var f = _floors.Values.Where(x => x.Kind == kind).OrderBy(x => x.Id).FirstOrDefault();
            if (f is null)
            {
                var id = kind == FloorKind.Header ? FloorKind.HeaderId : FloorKind.NavigationId;
                while (_floors.ContainsKey(id)) id += "_";     // mã bị mặt bằng thường chiếm (rất hiếm)
                var (dw, dh) = FloorKind.DefaultSize(kind);
                f = new FloorPlan { Id = id, Name = kind, Kind = kind, PanelWidth = dw, PanelHeight = dh, Enabled = false };
                _floors[id] = f;
                changed = true;
            }
            if (f.Name != kind) { f.Name = kind; changed = true; }                 // tên cố định
            if (f.Enabled is null) { f.Enabled = true; changed = true; }           // dữ liệu cũ: đã tạo = đang dùng
            if (f.IsStartup) { f.IsStartup = false; changed = true; }
            if (f.PanelWidth <= 0 || f.PanelHeight <= 0)
            {
                var (dw, dh) = FloorKind.DefaultSize(kind);
                if (f.PanelWidth <= 0) f.PanelWidth = dw;
                if (f.PanelHeight <= 0) f.PanelHeight = dh;
                changed = true;
            }
        }
        if (changed) SaveState();
    }

    public (FloorPlan? Floor, string? Error) UpdatePanel(string id, bool? enabled, int? width, int? height, bool? showAccount = null, bool? fullSize = null)
    {
        lock (_gate)
        {
            if (!_floors.TryGetValue(id, out var floor)) return (null, "Không tìm thấy mặt bằng.");
            if (!floor.IsPanel) return (null, "Chỉ Header / Navigation mới có thiết lập này.");
            int w = width ?? floor.PanelWidth, h = height ?? floor.PanelHeight;
            if (w < FloorKind.MinSize || w > FloorKind.MaxSize || h < FloorKind.MinSize || h > FloorKind.MaxSize)
                return (null, $"Chiều rộng / chiều cao phải trong khoảng {FloorKind.MinSize}–{FloorKind.MaxSize} px.");
            floor.PanelWidth = w; floor.PanelHeight = h;
            if (enabled.HasValue) floor.Enabled = enabled.Value;
            floor.ShowAccount = floor.Kind == FloorKind.Navigation && (showAccount ?? floor.ShowAccount);
            if (fullSize.HasValue) floor.FullSize = fullSize.Value;
            SaveState();
            return (floor, null);
        }
    }

    public FloorPlan? SetBackgroundColor(string id, string? color)
    {
        lock (_gate)
        {
            if (!_floors.TryGetValue(id, out var floor)) return null;
            floor.BackgroundColor = string.IsNullOrWhiteSpace(color) ? null : color.Trim().ToLowerInvariant();
            SaveState();
            return floor;
        }
    }

    public FloorPlan AddFloor(string id, string name)
    {
        var floor = _floors.GetOrAdd(id, _ => new FloorPlan { Id = id, Name = name });
        SaveState();
        return floor;
    }

    public FloorPlan? RenameFloor(string id, string name)
    {
        if (!_floors.TryGetValue(id, out var floor)) return null;
        if (floor.IsPanel) return floor;                 // Header / Navigation: tên cố định
        floor.Name = name;
        SaveState();
        return floor;
    }

    public FloorPlan? SetBackgroundImage(string id, string? url)
    {
        if (!_floors.TryGetValue(id, out var floor)) return null;
        floor.BackgroundImageUrl = url;
        SaveState();
        return floor;
    }

    public bool DeleteFloor(string id)
    {
        lock (_gate)
        {
            if (_floors.TryGetValue(id, out var existing) && existing.IsPanel) return false;   // Header / Navigation: mặc định, không xoá được
            var ok = _floors.TryRemove(id, out _);
            if (!ok) return false;
            SaveState();
            EnsureAtLeastOneFloor();   // xoá hết rồi -> tự tạo "Mặt bằng mặc định" (và là mặt bằng khởi động)
            NormalizeStartup();        // xoá đúng mặt bằng khởi động -> chuyển sang mặt bằng đầu danh sách
            return true;
        }
    }
}
