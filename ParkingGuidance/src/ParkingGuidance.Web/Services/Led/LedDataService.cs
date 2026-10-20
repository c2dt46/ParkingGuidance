using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Camera;
using ParkingGuidance.Web.Services.Sensor;
using ParkingGuidance.Web.Services.Tags;

namespace ParkingGuidance.Web.Services.Led;

/// <summary>Giá trị (số nguyên) của các biến MS, biến MC và project tag tại 1 thời điểm. Khoá = tên biến/tag, không phân biệt hoa/thường.</summary>
public sealed class LedDataSnapshot
{
    public IReadOnlyDictionary<string, long> Ms { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, long> Mc { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, long> Tags { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Giá trị theo tên; tên rỗng hoặc không có (biến/tag đã bị xoá, chưa tính được) = 0.</summary>
    public static long Lookup(IReadOnlyDictionary<string, long> map, string? name) =>
        !string.IsNullOrEmpty(name) && map.TryGetValue(name, out var v) ? v : 0;
}

/// <summary>1 nguồn dữ liệu để chọn trong vùng Number (biến MS, biến MC hoặc project tag).</summary>
public sealed class LedDataSourceItem
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Chỉ có với project tag: Integer / Boolean / Real.</summary>
    public string? Type { get; init; }
    public long Value { get; init; }
}

public sealed class LedDataSources
{
    public List<LedDataSourceItem> Ms { get; init; } = new();
    public List<LedDataSourceItem> Mc { get; init; } = new();
    public List<LedDataSourceItem> Tags { get; init; } = new();
}

public interface ILedDataService
{
    /// <summary>Giá trị các biến/tag mà vùng Number của <paramref name="cards"/> đang tham chiếu (dùng khi gửi card).</summary>
    LedDataSnapshot GetSnapshot(IEnumerable<LedCard> cards);

    /// <summary>Giá trị của TẤT CẢ biến MS, MC và project tag dạng số (cho giao diện xem trước).</summary>
    LedDataSnapshot GetAll();

    /// <summary>Danh sách để chọn trong giao diện: biến MS, biến MC, project tag kiểu Integer/Boolean/Real (kèm giá trị hiện tại).</summary>
    LedDataSources GetSources();
}

/// <summary>
/// Nguồn dữ liệu cho vùng Number của LED: giá trị hiển thị = biến MS + biến MC + project tag đã chọn (mục nào để trống tính là 0).
/// Giá trị luôn đọc trực tiếp từ các dịch vụ gốc (MS: cảm biến, MC: vị trí đỗ, tag: giá trị đang lưu), không giữ bản sao.
/// Project tag: Integer lấy nguyên giá trị, Boolean 0/1, Real làm tròn tới số nguyên gần nhất.
/// </summary>
public sealed class LedDataService : ILedDataService
{
    private readonly ISensorVariableService _ms;
    private readonly ICameraVariableService _mc;
    private readonly IProjectTagService _tags;

    public LedDataService(ISensorVariableService ms, ICameraVariableService mc, IProjectTagService tags)
    {
        _ms = ms;
        _mc = mc;
        _tags = tags;
    }

    public LedDataSnapshot GetSnapshot(IEnumerable<LedCard> cards)
    {
        var ms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
            foreach (var r in card.Areas)
            {
                if (r.Type != LedAreaType.Number) continue;
                if (!string.IsNullOrEmpty(r.NMs)) ms.Add(r.NMs);
                if (!string.IsNullOrEmpty(r.NMc)) mc.Add(r.NMc);
                if (!string.IsNullOrEmpty(r.NTag)) tags.Add(r.NTag);
            }
        return Build(ms, mc, tags);
    }

    public LedDataSnapshot GetAll() => Build(
        _ms.GetAll().Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
        _mc.GetAll().Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
        _tags.GetAll().Where(t => t.Type != TagDataType.String).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

    public LedDataSources GetSources() => new()
    {
        Ms = _ms.GetAll().Select(v => new LedDataSourceItem { Name = v.Name, Description = v.Description, Value = v.Value ?? 0 }).ToList(),
        Mc = _mc.GetAll().Select(v => new LedDataSourceItem { Name = v.Name, Description = v.Description, Value = v.Value ?? 0 }).ToList(),
        Tags = _tags.GetAll().Where(t => t.Type != TagDataType.String)
            .Select(t => new LedDataSourceItem { Name = t.Name, Description = t.Description, Type = t.Type.ToString(), Value = TagToLong(t) }).ToList()
    };

    private LedDataSnapshot Build(HashSet<string> ms, HashSet<string> mc, HashSet<string> tags)
    {
        var msValues = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var mcValues = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var tagValues = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (ms.Count > 0)
            foreach (var kv in _ms.GetValues(ms)) msValues[kv.Key] = kv.Value ?? 0;      // chua tinh duoc (null) -> 0
        if (mc.Count > 0)
            foreach (var kv in _mc.GetValues(mc)) mcValues[kv.Key] = kv.Value ?? 0;
        if (tags.Count > 0)
            foreach (var t in _tags.GetAll())
                if (tags.Contains(t.Name)) tagValues[t.Name] = TagToLong(t);

        return new LedDataSnapshot { Ms = msValues, Mc = mcValues, Tags = tagValues };
    }

    /// <summary>Giá trị số nguyên của 1 tag: Integer nguyên giá trị, Boolean 0/1, Real làm tròn; không đọc được = 0.</summary>
    private static long TagToLong(ProjectTag t)
    {
        var s = (t.Value ?? "").Trim();
        if (long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            && !double.IsNaN(d) && !double.IsInfinity(d))
            return (long)Math.Round(d, MidpointRounding.AwayFromZero);
        return 0;
    }
}
