using Microsoft.AspNetCore.SignalR;

namespace ParkingGuidance.Web.Hubs;

/// <summary>
/// Kênh SignalR để đẩy cập nhật realtime (sự kiện nhận diện, trạng thái ô đỗ)
/// lên trang Giám sát bãi xe và trang Tìm xe mà không cần client tự polling.
/// </summary>
public class MonitoringHub : Hub
{
}
