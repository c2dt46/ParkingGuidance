namespace ParkingGuidance.Web.Models;

/// <summary>
/// 1 camera vật lý đã được khai báo trong hệ thống (dò tìm được trên mạng hoặc thêm thủ công
/// theo IP). <see cref="IsCommitted"/> = false nghĩa là mới thêm vào danh sách, chưa chính thức
/// đưa vào vận hành; sau khi bấm "Thêm vào hệ thống bãi đỗ" (IsCommitted = true), camera này mới
/// được các thao tác về sau (giám sát, dịch vụ đọc camera nền...) sử dụng.
/// </summary>
public class ManagedCamera
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string IpAddress { get; set; } = "";
    public int Port { get; set; } = 80;
    public string Brand { get; set; } = "";
    public string? Model { get; set; }
    public string? OnvifServiceUrl { get; set; }

    /// <summary>Tầng hầm gắn camera này (mã mặt bằng, vd "B1").</summary>
    public string? Floor { get; set; }

    /// <summary>
    /// Zone (khu vực lắp đặt) của camera TRONG tầng hầm <see cref="Floor"/>: số tự nhiên nguyên dương (1, 2, 3...).
    /// Mỗi tầng có các Zone riêng (tầng B1 có Zone 1, tầng B2 cũng có Zone 1). Nhiều camera có thể cùng 1 Zone;
    /// số thứ tự các vị trí nhận diện (<see cref="DetectionZone.SlotId"/>) không được trùng trong cùng 1 Zone.
    /// </summary>
    public string? Zone { get; set; }
    public string? Notes { get; set; }

    /// <summary>Nguồn khung hình thực tế để dịch vụ nền đọc video: chỉ số webcam ("0"), đường dẫn
    /// file video, hoặc URL RTSP. Hệ thống dò được IP qua ONVIF nhưng CHƯA tự lấy được URL luồng
    /// RTSP (cần thêm bước gọi ONVIF Media Service — xem README) nên trường này cần nhập tay.</summary>
    public string? VideoSource { get; set; }

    public bool IsCommitted { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>Các vùng nhận diện (mỗi vùng ứng với 1 ô đỗ) đã vẽ cho camera này.</summary>
    public List<DetectionZone> Zones { get; set; } = new();

    /// <summary>"Automatic" = chạy trong vòng lặp nền tự động; "Manual" = KHÔNG tự chạy, chỉ nhận
    /// diện khi bấm nút "Nhận diện ngay".</summary>
    public string DetectionMode { get; set; } = "Automatic";

    /// <summary>
    /// Ngưỡng % sai khác hình ảnh so với khung hình trước để mới chạy nhận diện AI (0-100).
    /// Null = dùng giá trị mặc định toàn hệ thống (Detection:DefaultMotionThresholdPercent).
    /// Đặt cao hơn = tiết kiệm CPU/GPU hơn (bỏ qua nhiều khung hình tĩnh) nhưng phản ứng chậm hơn
    /// khi có thay đổi thật; đặt 0 = luôn chạy nhận diện mỗi khung hình (không tối ưu).
    /// </summary>
    public double? MotionThreshold { get; set; }

    // ---- Thiết lập giao diện của tab "Vị trí đỗ" cho camera này — lưu lại để mở lại vẫn như cũ ----

    /// <summary>"Camera" = lấy ảnh từ camera; "Pc" = chọn ảnh từ máy tính.</summary>
    public string UiSourceMode { get; set; } = "Pc";

    /// <summary>Chu kỳ tự lấy ảnh xem trước từ camera (giây) ở giao diện.</summary>
    public int UiCaptureIntervalSeconds { get; set; } = 10;

    /// <summary>Có đang bật "tự động lấy ảnh" ở giao diện hay không.</summary>
    public bool UiAutoCapture { get; set; }

    /// <summary>Ô "Tự động nhận diện khi ảnh mới thay đổi vượt ngưỡng".</summary>
    public bool UiAutoDetect { get; set; } = true;

    /// <summary>Ô "Ghi nhận kết quả vào bãi đỗ": kết quả nhận diện ở giao diện (ảnh từ PC / lấy tay) có cập nhật ô đỗ và bản đồ hay không.</summary>
    public bool UiRegisterToParking { get; set; } = true;
}

/// <summary>1 camera tìm thấy qua dò quét mạng (ONVIF WS-Discovery) — dữ liệu tạm thời, chưa lưu.</summary>
public class DiscoveredCamera
{
    public string IpAddress { get; set; } = "";
    public int Port { get; set; } = 80;
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string OnvifServiceUrl { get; set; } = "";

    /// <summary>True nếu IP này đã tồn tại trong danh sách camera của hệ thống.</summary>
    public bool AlreadyAdded { get; set; }
}
