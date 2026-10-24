namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Cho phép NẠP LẠI model OCR đọc cả chuỗi biển (<c>plate_ocr.onnx</c>, CRNN + CTC) khi ứng dụng đang chạy — dùng sau khi tab Train
/// huấn luyện xong model OCR mới, để áp dụng ngay mà không cần khởi động lại.
/// </summary>
public interface IOcrModelReloader
{
    /// <summary>Đường dẫn file model OCR đang được nạp (null nếu chưa có model OCR).</summary>
    string? CurrentOcrModelPath { get; }

    /// <summary>Nạp model OCR từ <paramref name="modelPath"/> thay cho model đang dùng. Thất bại thì model cũ vẫn giữ nguyên.</summary>
    bool ReloadOcr(string modelPath, out string message);
}
