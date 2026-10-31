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
}

/// <summary>Lưu trữ danh sách mặt bằng — tự tải lại khi khởi động và tự lưu sau mỗi thay đổi
/// qua IPersistenceService (file data/floors.json), không mất khi restart server.</summary>
public class FloorService : IFloorService
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

        var saved = _persistence.Load<List<FloorPlan>>(StateKey);
        if (saved is not null && saved.Count > 0)
        {
            foreach (var f in saved) _floors[f.Id] = f;
        }
        else
        {
            _floors["B1"] = new FloorPlan { Id = "B1", Name = "Tầng hầm B1", IsStartup = true };
            SaveState();
        }
        lock (_gate) NormalizeStartup();
    }

    private void SaveState() => _persistence.Save(StateKey, _floors.Values.ToList());

    public IReadOnlyList<FloorPlan> GetFloors()
    {
        lock (_gate)
        {
            EnsureAtLeastOneFloor();
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
        var keep = ordered.FirstOrDefault(f => f.IsStartup) ?? ordered[0];
        bool changed = false;
        foreach (var f in ordered)
        {
            var want = ReferenceEquals(f, keep);
            if (f.IsStartup != want) { f.IsStartup = want; changed = true; }
        }
        if (changed) SaveState();
    }

    public FloorPlan? SetStartup(string id)
    {
        lock (_gate)
        {
            if (!_floors.TryGetValue(id, out var target)) return null;
            foreach (var f in _floors.Values) f.IsStartup = ReferenceEquals(f, target);
            SaveState();
            return target;
        }
    }

    /// <summary>Luôn có ít nhất 1 mặt bằng: nếu danh sách trống thì tự tạo "Mặt bằng mặc định". Gọi trong lock(_gate).</summary>
    private void EnsureAtLeastOneFloor()
    {
        if (!_floors.IsEmpty) return;
        _floors[DefaultFloorId] = new FloorPlan { Id = DefaultFloorId, Name = DefaultFloorName, IsStartup = true };
        SaveState();
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
            var ok = _floors.TryRemove(id, out _);
            if (!ok) return false;
            SaveState();
            EnsureAtLeastOneFloor();   // xoá hết rồi -> tự tạo "Mặt bằng mặc định" (và là mặt bằng khởi động)
            NormalizeStartup();        // xoá đúng mặt bằng khởi động -> chuyển sang mặt bằng đầu danh sách
            return true;
        }
    }
}
