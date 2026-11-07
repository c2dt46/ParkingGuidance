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

    /// <summary>Chế độ mặt bằng: "Normal" (mặt bằng đỗ xe), "Header" (màn hình tiêu đề, góc trên-trái) hoặc
    /// "Navigation" (màn hình điều hướng, sát mép trái ngay dưới Header). Mỗi dự án chỉ có tối đa 1 Header và 1 Navigation.
    /// Header/Navigation luôn hiển thị khi chạy, không có cảm biến / camera / ô đỗ.</summary>
    public string Kind { get; set; } = FloorKind.Normal;

    /// <summary>Header / Navigation: có dùng màn hình này khi chạy hay không (luôn có sẵn trong dự án, tên cố định "Header" / "Navigation").
    /// null chỉ gặp ở dữ liệu cũ (đã tạo = đang dùng) và được FloorService chuẩn hoá. Mặt bằng thường: không dùng.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Màu nền (#RRGGBB) của mặt bằng / Header / Navigation. Rỗng = màu mặc định của giao diện.</summary>
    public string? BackgroundColor { get; set; }

    /// <summary>Kích thước (pixel) của màn hình Header / Navigation. Chỉ dùng khi Kind khác Normal.</summary>
    public int PanelWidth { get; set; }
    public int PanelHeight { get; set; }

    /// <summary>Chỉ cho Navigation: hiển thị khối tài khoản + nút Đăng xuất ở đáy màn hình Navigation (cùng kiểu thanh bên cũ).</summary>
    public bool ShowAccount { get; set; }

    /// <summary>Toàn kích thước: Header rộng hết màn hình (chiều cao theo PanelHeight); Navigation cao hết phần còn lại bên dưới Header
    /// (chiều rộng theo PanelWidth). Khi bật, PanelWidth của Header / PanelHeight của Navigation không còn tác dụng lúc chạy.</summary>
    public bool FullSize { get; set; }

    public bool IsPanel => Kind == FloorKind.Header || Kind == FloorKind.Navigation;
}

public static class FloorKind
{
    public const string Normal = "Normal";
    public const string Header = "Header";
    public const string Navigation = "Navigation";

    public static string Parse(string? s) =>
        string.Equals(s, Header, StringComparison.OrdinalIgnoreCase) ? Header :
        string.Equals(s, Navigation, StringComparison.OrdinalIgnoreCase) ? Navigation : Normal;

    public const int MinSize = 5, MaxSize = 4000;
    /// <summary>Mã + tên cố định của 2 màn hình mặc định (luôn có trong mọi dự án, không đổi tên / không xoá).</summary>
    public const string HeaderId = "HEADER", NavigationId = "NAVIGATION";
    public static (int W, int H) DefaultSize(string kind) => kind == Header ? (1920, 100) : (300, 980);
}
