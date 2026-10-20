using OpenCvSharp;
using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Camera;

public interface ICameraFrameProcessor
{
    /// <summary>
    /// Xử lý 1 khung hình của 1 camera: nhận diện xe, khớp với vùng nhận diện (nếu có) hoặc gán
    /// vào ô đỗ cố định (kiểu cũ), đọc biển số, ghi nhận vào ParkingService và phát realtime qua
    /// SignalR. Dùng chung bởi vòng lặp nền (CameraIngestionService) VÀ nút "Nhận diện ngay" (thủ
    /// công). Trả về số xe đã ghi nhận được vào ô đỗ.
    /// </summary>
    /// <param name="manual">true = nút "Nhận diện ngay": đọc biển ngay trên khung này (không đòi xe đứng yên qua nhiều lần xử lý,
    /// không bỏ qua OCR vì biển đã chốt) và ghi nhận tức thì (immediate) — không chờ nhiều lần đọc xác nhận.</param>
    Task<int> ProcessAsync(
        string cameraId, Mat frame, IReadOnlyList<DetectionZone> zones, string? legacySlotId, string? floor, string? zone, CancellationToken ct,
        bool manual = false);
}
