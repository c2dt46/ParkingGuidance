namespace ParkingGuidance.Web.Models;

/// <summary>
/// Các thông số cài đặt chung của hệ thống nhận diện — LƯU XUỐNG ĐĨA (settings.json) và có hiệu lực ngay khi
/// đổi, không cần khởi động lại. Giá trị mặc định lấy từ appsettings.json (mục "Detection") ở lần chạy đầu tiên.
/// </summary>
public class SystemSettings
{
    /// <summary>Chu kỳ lấy khung hình của MỖI camera ở chế độ tự động (giây).</summary>
    public double IntervalSeconds { get; set; } = 2;

    /// <summary>Ngưỡng % thay đổi ảnh mặc định để mới chạy AI (camera nào không đặt riêng thì dùng giá trị này).</summary>
    public double DefaultMotionThresholdPercent { get; set; } = 4.0;

    /// <summary>Độ tin cậy tối thiểu của YOLO để tính là có xe (0-1).</summary>
    public float ConfidenceThreshold { get; set; } = 0.25f;

    /// <summary>Thời gian tối đa chờ mở kết nối 1 camera (giây).</summary>
    public double OpenTimeoutSeconds { get; set; } = 8;

    /// <summary>Thời gian tối đa chờ lấy 1 khung hình (giây).</summary>
    public double FrameReadTimeoutSeconds { get; set; } = 5;

    /// <summary>Camera lỗi sẽ được thử lại với khoảng nghỉ tăng dần, tối đa bằng giá trị này (giây).</summary>
    public double MaxRetryBackoffSeconds { get; set; } = 60;

    /// <summary>Nếu hàng đợi AI có xe bị chờ lâu hơn mức này (giây) thì ghi cảnh báo "hệ thống quá tải".</summary>
    public double QueueWarnSeconds { get; set; } = 30;

    /// <summary>Số lần xử lý liên tiếp KHÔNG thấy xe trong 1 vùng thì mới xác nhận xe đã rời đi (tránh 1 khung hình nhận thiếu xe
    /// làm ghi nhận sai "xe ra"). Khi đang chờ xác nhận, camera được xử lý mỗi chu kỳ dù ảnh không đổi.</summary>
    public int VacancyConfirmFrames { get; set; } = 3;

    // ---- Thông số nhận diện biển số (các trường nullable: trang khác không gửi thì giữ giá trị đang lưu; null = dùng mặc định) ----

    /// <summary>Độ tin cậy tối thiểu của bộ dò KHUNG BIỂN SỐ (0.05–0.95, mặc định 0.4). Thấp = dò được nhiều biển hơn nhưng dễ có khung rác.</summary>
    public float? PlateBoxConfidence { get; set; }

    /// <summary>Bộ dò khung biển số: 0 = tự động (gộp khung của YOLO11n đã train và Darknet YOLOv3-tiny), 1 = chỉ YOLO11n đã train (nếu chưa có model thì dùng Darknet),
    /// 2 = chỉ Darknet YOLOv3-tiny. Dùng để so sánh độ chính xác của khung biển giữa hai bộ dò.</summary>
    public int? PlateDetector { get; set; }

    /// <summary>Kết hợp Tesseract với bộ đọc ký tự YOLO: 0 = chỉ YOLO (Tesseract chỉ dự phòng khi YOLO không đọc được), 1 = thêm Tesseract làm bước KIỂM CHỨNG
    /// khi YOLO chưa chắc chắn (mặc định). Tesseract không bao giờ lật ngược một kết quả YOLO đã chắc.</summary>
    public int? CharCombine { get; set; }

    /// <summary>Độ tin cậy trung bình tối thiểu của CÁC KÝ TỰ (0–0.95, mặc định 0.5). Lần đọc thấp hơn mức này bị bỏ, không tham gia bỏ phiếu.</summary>
    public float? CharConfidence { get; set; }

    /// <summary>Tỉ lệ diện tích khung biển phải nằm TRONG khung xe (0–1, mặc định 0.1). Biển nằm hoàn toàn ngoài mọi xe = không hợp lệ.
    /// 0 = tắt luật này (chỉ ảnh hưởng chế độ dò biển trên cả khung khi không thấy xe nào ở tab Mô phỏng).</summary>
    public float? PlateMinOverlap { get; set; }

    /// <summary>Cạnh dài tối đa (px) của ảnh đưa vào bước dò xe/dò khung biển; ảnh lớn hơn sẽ thu nhỏ trước khi dò (nhanh hơn, kém chính xác hơn).
    /// 0 = dùng ảnh gốc. Việc ĐỌC ký tự luôn dùng ảnh gốc.</summary>
    public int? ProcessMaxSide { get; set; }

    /// <summary>Chiều cao (px) mà ảnh biển được phóng/thu về trước khi đọc ký tự (40–300, mặc định 110). Cao hơn = chi tiết hơn nhưng chậm hơn.</summary>
    public int? PlateReadHeight { get; set; }

    /// <summary>1 = xe nào KHÔNG có khung biển do bộ dò tìm thấy thì vẫn chạy tìm biển đầy đủ (chậm, tới vài giây/xe); 0 = bỏ qua ngay (mặc định, nhanh).</summary>
    public int? PlateFullSearch { get; set; }

    /// <summary>Cách tính KHUNG BIỂN SỐ hiển thị/ghi nhận sau khi đọc ký tự: 0 = giữ nguyên khung do bộ dò biển số tìm ra (mặc định),
    /// 1 = theo kích thước dải ký tự đã nhận diện (khung bao các ký tự + lề). Có hiệu lực ngay khi đổi.</summary>
    public int? PlateBoxMode { get; set; }

    /// <summary>Khi PlateBoxMode = 0: nới khung biển (ôm sát như tab Train biển số) ra mỗi phía thêm X% chiều rộng/chiều cao khung (0–30, mặc định 4).</summary>
    public float? PlateBoxOffsetPercent { get; set; }

    /// <summary>Thời gian tối đa chờ card/SDK phản hồi khi gửi dữ liệu tới LED hoặc kiểm tra kết nối (giây). Quá hạn thì bridge bị kill và tự khởi động lại.
    /// Có hiệu lực ngay khi đổi. null = chưa đặt (giữ giá trị đang lưu; các trang khác không gửi trường này sẽ không làm mất giá trị).</summary>
    public double? LedRequestTimeoutSeconds { get; set; }

    /// <summary>Trình duyệt dùng để mở giao diện Giám sát: "default" (mặc định hệ thống), "chrome", "edge", "firefox", "safari", "brave", "opera".
    /// Nếu trình duyệt được chọn không tồn tại trên máy thì tự dùng trình duyệt mặc định. null = chưa đặt (giữ giá trị đang lưu).</summary>
    public string? PreferredBrowser { get; set; }

    /// <summary>Giao diện dùng chung cho mọi máy (chọn "Áp dụng cho mọi máy" ở Người dùng &gt; Giao diện &amp; ngôn ngữ).
    /// null = chưa đặt (giữ giá trị đang lưu; các trang khác không gửi trường này sẽ không làm mất giá trị).</summary>
    public UiPreferences? Ui { get; set; }

    /// <summary>Đưa các giá trị về miền hợp lệ — dùng khi nhận dữ liệu từ giao diện hoặc file bị sửa tay.</summary>
    public SystemSettings Sanitized() => new()
    {
        IntervalSeconds = Math.Clamp(IntervalSeconds, 0.5, 3600),
        DefaultMotionThresholdPercent = Math.Clamp(DefaultMotionThresholdPercent, 0, 100),
        ConfidenceThreshold = Math.Clamp(ConfidenceThreshold, 0.05f, 0.95f),
        OpenTimeoutSeconds = Math.Clamp(OpenTimeoutSeconds, 1, 120),
        FrameReadTimeoutSeconds = Math.Clamp(FrameReadTimeoutSeconds, 1, 120),
        MaxRetryBackoffSeconds = Math.Clamp(MaxRetryBackoffSeconds, 1, 3600),
        QueueWarnSeconds = Math.Clamp(QueueWarnSeconds, 1, 3600),
        VacancyConfirmFrames = Math.Clamp(VacancyConfirmFrames, 1, 20),
        PlateBoxConfidence = Math.Clamp(PlateBoxConfidence ?? 0.4f, 0.05f, 0.95f),
        CharConfidence = Math.Clamp(CharConfidence ?? 0.5f, 0f, 0.95f),
        PlateDetector = Math.Clamp(PlateDetector ?? 0, 0, 2),
        CharCombine = (CharCombine ?? 1) != 0 ? 1 : 0,
        PlateMinOverlap = Math.Clamp(PlateMinOverlap ?? 0.1f, 0f, 1f),
        ProcessMaxSide = (ProcessMaxSide ?? 0) <= 0 ? 0 : Math.Clamp(ProcessMaxSide!.Value, 320, 4096),
        PlateReadHeight = Math.Clamp(PlateReadHeight ?? 110, 40, 300),
        PlateFullSearch = (PlateFullSearch ?? 0) != 0 ? 1 : 0,
        PlateBoxMode = (PlateBoxMode ?? 0) != 0 ? 1 : 0,
        PlateBoxOffsetPercent = Math.Clamp(PlateBoxOffsetPercent ?? 4f, 0f, 30f),
        LedRequestTimeoutSeconds = Math.Clamp(LedRequestTimeoutSeconds ?? 15, 1, 120),
        PreferredBrowser = string.IsNullOrWhiteSpace(PreferredBrowser) ? "default" : PreferredBrowser.Trim().ToLowerInvariant(),
        Ui = (Ui ?? new UiPreferences()).Sanitized()
    };
}
