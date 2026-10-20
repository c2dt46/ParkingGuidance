using System.Collections.Concurrent;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Layout;

public interface ILayoutService
{
    IReadOnlyList<LayoutElement> GetElements(string floor);
    LayoutElement AddElement(LayoutElement element);
    LayoutElement? UpdateElement(string id, Action<LayoutElement> apply);
    bool DeleteElement(string id);
}

/// <summary>Lưu trữ các đối tượng layout — tự tải lại khi khởi động và tự lưu sau mỗi thay đổi
/// qua IPersistenceService (file data/layout-elements.json), không mất khi restart server.</summary>
public class LayoutService : ILayoutService
{
    private readonly ConcurrentDictionary<string, LayoutElement> _elements = new();
    private readonly IPersistenceService _persistence;
    private const string StateKey = "layout-elements";

    public LayoutService(IPersistenceService persistence)
    {
        _persistence = persistence;

        var saved = _persistence.Load<List<LayoutElement>>(StateKey);
        if (saved is not null)
        {
            foreach (var el in saved)
                _elements[el.Id] = el;
            MigrateSlotKeys();
        }
    }

    /// <summary>Đối tượng vùng nhận diện cũ trỏ tới khoá ô kiểu cũ ("1", "B1-001", "B1-1"...) → khoá mới "{tầng}-{zone}-{số}" với Zone mặc định
    /// (cùng quy tắc với ParkingService). Khoá đã ở dạng mới được giữ nguyên.</summary>
    private void MigrateSlotKeys()
    {
        bool changed = false;
        foreach (var el in _elements.Values.Where(e => e.Type == LayoutElementType.ParkingSlot && !string.IsNullOrWhiteSpace(e.SlotId)))
        {
            if (SlotKey.TryParseForFloor(el.SlotId, el.Floor, out _, out _)) continue; // da la khoa dang moi
            if (!SlotKey.TryExtractLegacyNumber(el.SlotId, out var n)) continue;
            var key = SlotKey.Make(el.Floor, SlotKey.DefaultZone, n);
            if (key == el.SlotId) continue;
            el.SlotId = key;
            changed = true;
        }
        if (changed) SaveState();
    }

    private void SaveState() => _persistence.Save(StateKey, _elements.Values.ToList());

    public IReadOnlyList<LayoutElement> GetElements(string floor) =>
        _elements.Values.Where(e => e.Floor == floor).ToList();

    public LayoutElement AddElement(LayoutElement element)
    {
        _elements[element.Id] = element;
        SaveState();
        return element;
    }

    public LayoutElement? UpdateElement(string id, Action<LayoutElement> apply)
    {
        if (!_elements.TryGetValue(id, out var element))
            return null;

        apply(element);
        SaveState();
        return element;
    }

    public bool DeleteElement(string id)
    {
        var ok = _elements.TryRemove(id, out _);
        if (ok) SaveState();
        return ok;
    }
}
