using OpenCvSharp;
using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Detection;

public interface IVehicleDetectionService
{
    /// <summary>Cho biết model AI đã được nạp thành công hay chưa (dùng để cảnh báo người dùng).</summary>
    bool IsReady { get; }

    /// <summary>Đường dẫn model đang cấu hình trong appsettings.json (model gốc, ví dụ models/yolo11n.onnx).</summary>
    string ModelPath { get; }

    /// <summary>Đường dẫn thực tế đã tìm thấy và nạp model (null nếu không tìm thấy file nào).</summary>
    string? ResolvedModelPath { get; }

    /// <summary>Danh sách các vị trí đã thử tìm file model — hữu ích để debug khi không tìm thấy.</summary>
    IReadOnlyList<string> SearchedPaths { get; }

    /// <summary>Thông báo lỗi cuối cùng khi nạp model thất bại (null nếu chưa gặp lỗi).</summary>
    string? LoadError { get; }

    /// <summary>Mô tả model đang chạy (tên file, kích thước ảnh vào, số lớp, có phải model đã train thêm không) để hiển thị.</summary>
    string ModelInfo { get; }

    /// <summary>true = đang dùng model đã train thêm ở tab "Train xe" (data/vehicle-train/active/vehicle.onnx).</summary>
    bool UsingCustomModel { get; }

    /// <summary>Đường dẫn model đã train thêm (có thể chưa tồn tại). Cài model mới = chép file vào đây rồi gọi <see cref="Reload"/>.</summary>
    string CustomModelPath { get; }

    /// <summary>Nạp lại model theo thứ tự ưu tiên: model đã train thêm (nếu có file) → model gốc. Không cần khởi động lại ứng dụng.
    /// Nạp thất bại thì giữ nguyên model đang chạy.</summary>
    bool Reload(out string message);

    /// <summary>Phát hiện các phương tiện (car/truck/bus) trong 1 khung hình (xe máy chỉ khi bật Detection:IncludeMotorcycle).
    /// Nếu confThreshold = null, dùng ngưỡng cấu hình mặc định (Detection:ConfidenceThreshold).</summary>
    List<DetectionResult> Detect(Mat frame, float? confThreshold = null);

    /// <summary>Như <see cref="Detect"/> nhưng LUÔN gồm cả xe máy — dùng để gợi ý khung khi gán nhãn ở tab "Train xe".</summary>
    List<DetectionResult> DetectAll(Mat frame, float confThreshold);
}
