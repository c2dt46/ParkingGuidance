namespace ParkingGuidance.Web.Models;

/// <summary>Toàn bộ trạng thái bãi xe tại 1 thời điểm — dùng để lưu/khôi phục qua IPersistenceService.</summary>
public class ParkingStateSnapshot
{
    public List<ParkingSlot> Slots { get; set; } = new();
    public List<CameraDevice> Cameras { get; set; } = new();
    public List<LedBoard> Leds { get; set; } = new();
    public List<ParkingSession> ActiveSessions { get; set; } = new();
    public List<ParkingSession> History { get; set; } = new();
    public List<ParkingEvent> Events { get; set; } = new();
}
