using OpenCvSharp;
using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Anpr;

public interface IPlateRecognitionService
{
    /// <summary>Cho biết engine OCR (Tesseract) đã được nạp thành công hay chưa.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Nhận vùng ảnh chứa 1 xe, dò tìm vùng biển số bên trong và đọc chữ bằng OCR.
    /// Trả về (biển số, độ tin cậy 0-1, khung biển số TÍNH THEO TOẠ ĐỘ CỦA ẢNH XE ĐÃ CẮT)
    /// hoặc (null, 0, null) nếu không tìm được vùng biển. (null, 0, khung) = tìm được vùng biển nhưng CHƯA đọc được ký tự
    /// (khung là vùng biển khả dĩ nhất do bộ dò tìm ra — để người dùng chỉnh/nhập tay ở tab Train).
    /// </summary>
    (string? PlateNumber, float Confidence, BoundingBox? PlateBox) Recognize(Mat vehicleCrop);

    /// <summary>
    /// Dò nhanh trên CẢ KHUNG HÌNH khi model dò xe không thấy xe nào. Chỉ dùng bộ dò biển số đã huấn luyện (không có biển → trả về ngay,
    /// không chạy lần dò nới lỏng / tìm ứng viên bằng xử lý ảnh / Tesseract trên các vùng rác) và có giới hạn thời gian.
    /// Kết quả cùng định dạng với <see cref="Recognize"/>; khung biển tính theo toạ độ của chính ảnh truyền vào.
    /// </summary>
    (string? PlateNumber, float Confidence, BoundingBox? PlateBox) RecognizeQuick(Mat frame);

    /// <summary>
    /// Chỉ DÒ KHUNG BIỂN SỐ trên cả khung hình bằng bộ dò (không đọc ký tự, không chia ô, không dự phòng chậm) — chạy song song với bước dò xe.
    /// Khung trả về theo toạ độ của chính ảnh truyền vào. Ngưỡng lấy từ cài đặt chung (PlateBoxConfidence).
    /// </summary>
    List<BoundingBox> DetectPlateBoxes(Mat frame);

    /// <summary>
    /// Đọc ký tự tại 1 khung biển đã dò được (toạ độ của <paramref name="frame"/>). Trả về (biển, độ tin cậy, khung biển tính lại theo ký tự — cùng hệ toạ độ);
    /// không đọc được: (null, 0, khung gốc).
    /// </summary>
    (string? PlateNumber, float Confidence, BoundingBox? PlateBox) ReadPlateAt(Mat frame, BoundingBox plateBox);
}
