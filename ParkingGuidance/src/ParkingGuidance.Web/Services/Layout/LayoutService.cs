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

    /// <summary>Xoá mọi đối tượng của 1 mặt bằng (và nút ấn ở mặt bằng khác đang trỏ tới nó thì bỏ hành động chuyển).
    /// Trả về danh sách URL ảnh (ảnh upload) của các đối tượng đã xoá để nơi gọi dọn file.</summary>
    IReadOnlyList<string> RemoveFloor(string floor);
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

    public IReadOnlyList<string> RemoveFloor(string floor)
    {
        var norm = SlotKey.NormalizeFloor(floor);
        var urls = new List<string>();
        bool changed = false;

        foreach (var el in _elements.Values.ToList())
        {
            if (SlotKey.NormalizeFloor(el.Floor) == norm)
            {
                if (_elements.TryRemove(el.Id, out var removed))
                {
                    changed = true;
                    foreach (var u in new[] { removed.ImageUrl, removed.StateImage0, removed.StateImage1 })
                        if (!string.IsNullOrWhiteSpace(u)) urls.Add(u!);
                }
            }
            else if (!string.IsNullOrWhiteSpace(el.TargetFloorId)
                     && SlotKey.NormalizeFloor(el.TargetFloorId) == norm)
            {
                el.TargetFloorId = null;   // nut an o mat bang khac tro toi mat bang da xoa -> bo hanh dong
                changed = true;
            }
        }

        if (changed) SaveState();

        // Chi tra ve anh KHONG con doi tuong nao (o mat bang khac) dung chung, de khong xoa nham file dang duoc dung
        var stillUsed = _elements.Values
            .SelectMany(e => new[] { e.ImageUrl, e.StateImage0, e.StateImage1 })
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return urls.Where(u => !stillUsed.Contains(u)).Distinct().ToList();
    }

    public bool DeleteElement(string id)
    {
        var ok = _elements.TryRemove(id, out _);
        if (ok) SaveState();
        return ok;
    }
}
