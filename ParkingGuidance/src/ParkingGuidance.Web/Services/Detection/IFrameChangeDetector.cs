using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Detection;

/// <summary>Kết quả so sánh 1 khung hình với khung hình tham chiếu trước đó.</summary>
/// <param name="ShouldProcess">true nếu NÊN chạy nhận diện AI.</param>
/// <param name="ChangePercent">% sai khác (0-100). 0 khi là khung đầu tiên hoặc khi ngưỡng = 0 (tắt tối ưu).</param>
/// <param name="IsFirstFrame">true nếu chưa có khung tham chiếu (lần đầu cho key này).</param>
public readonly record struct FrameChangeResult(bool ShouldProcess, double ChangePercent, bool IsFirstFrame);

public interface IFrameChangeDetector
{
    /// <summary>
    /// So sánh khung hình hiện tại với khung hình gần nhất đã xử lý CỦA CÙNG "key" (thường là
    /// CameraId), trả về true nếu độ sai khác đạt ngưỡng — nghĩa là NÊN chạy nhận diện AI cho
    /// khung hình này. Nếu chưa đạt ngưỡng, bỏ qua khung hình (không tốn CPU/GPU chạy YOLO+OCR
    /// cho 1 khung hình gần như không đổi so với lần trước).
    /// Luôn trả về true ở lần gọi đầu tiên cho 1 key (chưa có khung hình trước để so sánh).
    /// </summary>
    bool ShouldProcess(string key, Mat frame, double thresholdPercent);

    /// <summary>Như <see cref="ShouldProcess"/> nhưng trả thêm % sai khác để hiển thị/ghi log.</summary>
    FrameChangeResult Evaluate(string key, Mat frame, double thresholdPercent);

    /// <summary>
    /// Chốt khung vừa được <see cref="Evaluate"/> cho phép xử lý (ShouldProcess = true) làm ẢNH THAM CHIẾU mới.
    /// Gọi SAU KHI nhận diện thành công; nếu nhận diện lỗi thì KHÔNG gọi — ảnh tham chiếu giữ nguyên nên
    /// khung hình kế tiếp vẫn vượt ngưỡng và được thử nhận diện lại. Không làm gì nếu không có khung đang chờ chốt.
    /// </summary>
    void CommitReference(string key);

    /// <summary>
    /// Đặt THẲNG khung <paramref name="frame"/> làm ảnh tham chiếu của <paramref name="key"/>, không so sánh, không phụ thuộc ngưỡng
    /// (kể cả ngưỡng = 0) và huỷ khung đang chờ chốt. Dùng cho nút "Nhận diện ngay" (chế độ Thủ công): ảnh vừa nhận diện được lưu lại
    /// để các lần so sánh tỷ lệ sai khác sau đó (chế độ Tự động / nạp ảnh) lấy làm mốc.
    /// </summary>
    void SetReference(string key, Mat frame);
}
