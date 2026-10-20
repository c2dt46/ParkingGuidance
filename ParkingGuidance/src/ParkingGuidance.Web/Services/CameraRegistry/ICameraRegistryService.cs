using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.CameraRegistry;

public interface ICameraRegistryService
{
    /// <summary>Xảy ra SAU khi thông tin camera (tầng / Zone), vùng nhận diện (vị trí đỗ) hoặc danh sách camera thay đổi
    /// (sửa thông tin, lưu vùng nhận diện, xoá camera). Trình xử lý phải nhẹ và không ném lỗi.</summary>
    event Action? Changed;

    IReadOnlyList<ManagedCamera> GetAll();
    ManagedCamera? Get(string id);
    bool IsIpRegistered(string ipAddress, int port);

    /// <summary>Thêm 1 camera mới (từ dò quét hoặc nhập tay). Ném lỗi nếu IP:Port đã tồn tại.</summary>
    ManagedCamera Add(string ipAddress, int port, string brand, string? model, string? onvifServiceUrl);

    /// <summary>Cập nhật thông tin camera: tầng hầm + Zone (khu vực lắp đặt trong tầng) + ghi chú + nguồn video.</summary>
    ManagedCamera? UpdateInfo(string id, string? floor, string? zone, string? notes, string? videoSource);

    /// <summary>Thay toàn bộ danh sách vùng nhận diện của 1 camera (tab "Vị trí đỗ").</summary>
    ManagedCamera? UpdateZones(string id, List<DetectionZone> zones);

    ManagedCamera? UpdateMotionThreshold(string id, double? motionThreshold);

    ManagedCamera? UpdateDetectionMode(string id, string mode);

    /// <summary>Lưu thiết lập giao diện của tab Vị trí đỗ (nguồn ảnh, chu kỳ tự lấy ảnh, tự nhận diện).</summary>
    ManagedCamera? UpdateUiSettings(string id, string sourceMode, int captureIntervalSeconds, bool autoCapture, bool autoDetect, bool registerToParking);

    /// <summary>Đánh dấu camera đã chính thức đưa vào hệ thống bãi đỗ (dùng cho các thao tác về sau).</summary>
    ManagedCamera? Commit(string id);

    bool Delete(string id);
}
