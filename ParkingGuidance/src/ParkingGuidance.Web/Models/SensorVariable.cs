using System.Text.Json.Serialization;

namespace ParkingGuidance.Web.Models;

/// <summary>
/// Trạng thái mà 1 biến MS đếm. Tên gọi theo ý nghĩa bãi đỗ:
/// Free = "Có chỗ đỗ" (cảm biến báo Trống), Occupied = "Không có chỗ đỗ" (cảm biến báo Có xe), Error = "Lỗi".
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SensorCountState
{
    Free = 0,
    Occupied = 1,
    Error = 2
}

/// <summary>Các cảm biến được chọn trên 1 thiết bị (ZCU của MGT200 hoặc Keytop).</summary>
public sealed class SensorPick
{
    /// <summary>Số thứ tự thiết bị bắt đầu từ 0 (ZCU 0, 1, 2…).</summary>
    public int Device { get; set; }

    /// <summary>
    /// Mỗi ký tự '0'/'1' là 1 cảm biến, đúng thứ tự ký tự của <c>SensorDeviceView.States</c>:
    /// MGT200 160 ký tự (chỉ số = (Line−1)×32 + ID); Keytop 60 ký tự (chỉ số = cảm biến − 1). '1' = được chỉ định.
    /// </summary>
    public string Mask { get; set; } = "";
}

/// <summary>
/// Biến dữ liệu loại MS (liên quan tới cảm biến). Thay vì mỗi cảm biến / mỗi ô đỗ một biến, 1 biến chỉ đếm MỘT trạng thái
/// trên tập cảm biến được chỉ định: Giá trị = số cảm biến (trong tập đó) đang ở trạng thái <see cref="Count"/>.
/// Lưu ở data/sensor-variables.json.
/// </summary>
public class SensorVariable
{
    /// <summary>Tên biến do hệ thống tự đặt: MS0, MS1, MS2… (chỉ số nhỏ nhất còn trống). Không đổi được.</summary>
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Trạng thái mà biến đếm. null = chưa xác định (biến vừa thêm). Khi đã xác định thì KHÔNG đổi được nữa (muốn đổi: xoá biến, thêm lại).</summary>
    public SensorCountState? Count { get; set; }

    /// <summary>Loại cảm biến mà phần chọn áp dụng: "Pm200" (MGT200) hoặc "Keytop". Chỉ có giá trị khi đang dùng đúng loại này.</summary>
    public string Source { get; set; } = "Pm200";
    public List<SensorPick> Selection { get; set; } = new();

    public SensorVariable Clone() => new()
    {
        Name = Name,
        Description = Description,
        Count = Count,
        Source = Source,
        Selection = Selection.Select(p => new SensorPick { Device = p.Device, Mask = p.Mask }).ToList()
    };
}
