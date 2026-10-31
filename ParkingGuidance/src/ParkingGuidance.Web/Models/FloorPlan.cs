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

    /// <summary>Mặt bằng khởi động: được chọn đầu tiên khi mở trang Giám sát. Luôn có ĐÚNG 1 mặt bằng như vậy
    /// (FloorService tự giữ điều này; khi chưa có mặt bằng nào thì "Mặt bằng mặc định" là mặt bằng khởi động).</summary>
    public bool IsStartup { get; set; }
}
