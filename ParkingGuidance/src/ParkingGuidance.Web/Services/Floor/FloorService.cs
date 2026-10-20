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
    bool DeleteFloor(string id);
}

/// <summary>Lưu trữ danh sách mặt bằng — tự tải lại khi khởi động và tự lưu sau mỗi thay đổi
/// qua IPersistenceService (file data/floors.json), không mất khi restart server.</summary>
public class FloorService : IFloorService
{
    private readonly ConcurrentDictionary<string, FloorPlan> _floors = new();
    private readonly IPersistenceService _persistence;
    private const string StateKey = "floors";

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
            _floors["B1"] = new FloorPlan { Id = "B1", Name = "Tầng hầm B1" };
            SaveState();
        }
    }

    private void SaveState() => _persistence.Save(StateKey, _floors.Values.ToList());

    public IReadOnlyList<FloorPlan> GetFloors() => _floors.Values.OrderBy(f => f.Id).ToList();

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
        var ok = _floors.TryRemove(id, out _);
        if (ok) SaveState();
        return ok;
    }
}
