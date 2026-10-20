namespace ParkingGuidance.Web.Models;

/// <summary>
/// 1 mặt bằng (tầng) trong dự án, ví dụ "B1", "B2"... Mỗi mặt bằng có thể có 1 ảnh nền
/// (sơ đồ/bản vẽ mặt bằng thực tế) để làm nền cho Layout Editor trên trang Giám sát.
/// </summary>
public class FloorPlan
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? BackgroundImageUrl { get; set; }
}
