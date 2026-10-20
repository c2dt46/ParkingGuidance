namespace ParkingGuidance.Web.Models;

/// <summary>1 camera giám sát gắn với 1 hoặc nhiều ô đỗ.</summary>
public class CameraDevice
{
    public string CameraId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Floor { get; set; } = "";

    /// <summary>Nguồn khung hình: chỉ số webcam ("0"), đường dẫn video, hoặc URL RTSP.</summary>
    public string Source { get; set; } = "";
    public List<string> CoveredSlotIds { get; set; } = new();
    public bool IsOnline { get; set; } = true;
}

/// <summary>1 bảng LED chỉ hướng còn chỗ trống trên tầng.</summary>
public class LedBoard
{
    public string LedId { get; set; } = "";
    public string Floor { get; set; } = "";
    public Dictionary<string, int> DirectionCounts { get; set; } = new();
    public bool IsOnline { get; set; } = true;
}

/// <summary>1 sự kiện do camera ghi nhận (xe vào/ra, quét lại biển số...).</summary>
public class ParkingEvent
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string CameraId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PlateNumber { get; set; } = "";
    public string SlotId { get; set; } = "";
}

/// <summary>1 phiên đỗ xe: từ lúc vào ô đỗ tới lúc rời đi.</summary>
public class ParkingSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string PlateNumber { get; set; } = "";
    public string SlotId { get; set; } = "";
    public DateTime EntryTime { get; set; } = DateTime.Now;
    public DateTime? ExitTime { get; set; }
    public bool IsVerified { get; set; }

    /// <summary>Độ tin cậy OCR (0-1) của lần đọc biển số tốt nhất cho phiên này; null với phiên nhập tay/dữ liệu cũ.</summary>
    public float? PlateConfidence { get; set; }

    public List<ParkingEvent> Events { get; set; } = new();

    public bool IsActive => ExitTime is null;
}
