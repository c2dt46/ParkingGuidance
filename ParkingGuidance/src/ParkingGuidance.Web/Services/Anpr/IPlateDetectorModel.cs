using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Bộ DÒ VÙNG BIỂN SỐ (bước 1 của đọc biển) — cho phép tab "Train biển số" gợi ý khung, đo, cài và NẠP LẠI model khi chương trình đang chạy.
/// Model đã train thêm nằm ở <c>{DataDirectory}/plate-train/active/plate.onnx</c> và được ƯU TIÊN dùng trước bộ dò mặc định
/// (YOLOv3-tiny Darknet / plate_detector.onnx); không thấy biển nào thì vẫn rơi về bộ dò mặc định như cũ.
/// </summary>
public interface IPlateDetectorModel
{
    /// <summary>Có ít nhất một bộ dò biển (đã train thêm hoặc mặc định) đang nạp.</summary>
    bool IsReady { get; }

    /// <summary>Mô tả bộ dò đang chạy để hiển thị (tên, có phải model đã train thêm không).</summary>
    string ModelInfo { get; }

    /// <summary>true = đang dùng model đã train thêm ở tab "Train biển số".</summary>
    bool UsingCustomModel { get; }

    /// <summary>Đường dẫn model đã train thêm (có thể chưa tồn tại). Cài model mới = chép file vào đây rồi gọi <see cref="Reload"/>.</summary>
    string CustomModelPath { get; }

    /// <summary>Đường dẫn model ONNX biển số gốc (plate_detector.onnx) nếu có — dùng làm mốc so sánh khi train. null nếu bộ dò mặc định là Darknet / không có.</summary>
    string? BaseModelPath { get; }

    /// <summary>Thông báo lỗi cuối cùng khi nạp model đã train thêm thất bại (null nếu không có lỗi).</summary>
    string? LoadError { get; }

    /// <summary>Nạp lại model đã train thêm (nếu có file; không có file = bỏ model đã train thêm, quay về bộ dò mặc định).
    /// Nạp thất bại thì giữ nguyên model đang chạy.</summary>
    bool Reload(out string message);

    /// <summary>
    /// Dò khung biển trên 1 ảnh (ảnh cả khung hình hoặc ảnh xe) để GỢI Ý khung cho người gán nhãn — khung ôm sát biển, không nới viền.
    /// Trả về toạ độ pixel của chính ảnh truyền vào (x1, y1, x2, y2, điểm).
    /// </summary>
    List<(double X1, double Y1, double X2, double Y2, float Score)> SuggestBoxes(Mat image, float confThreshold);
}
