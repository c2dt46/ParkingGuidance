namespace ParkingGuidance.Web.Models;

/// <summary>1 ô đỗ xe trên bản đồ tầng (ví dụ "B1-006").</summary>
public class ParkingSlot
{
    public string SlotId { get; set; } = "";
    public string Floor { get; set; } = "";
    public string Zone { get; set; } = "";
    public string CameraId { get; set; } = "";
    public string LedId { get; set; } = "";
    public SlotStatus Status { get; set; } = SlotStatus.Empty;
    public string? PlateNumber { get; set; }

    /// <summary>Thời điểm xe hiện tại vào ô đỗ (chỉ có ý nghĩa khi Status = Occupied).</summary>
    public DateTime? ParkedAt { get; set; }

    /// <summary>Mô tả lỗi (chỉ có ý nghĩa khi Status = Error) — ví dụ "Camera mất kết nối".</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Số thứ tự của ô trong tầng (số tự nhiên: 1, 2, 3...) — phần hiển thị; <see cref="SlotId"/> là khoá "{tầng}-{số}".</summary>
    public string Number { get; set; } = "";

    /// <summary>Biển số của xe GẦN NHẤT đã rời khỏi ô đỗ này ("UNKNOWN" nếu lúc đó chưa đọc được biển).</summary>
    public string? LastExitPlate { get; set; }

    /// <summary>Thời điểm xe gần nhất rời khỏi ô đỗ này.</summary>
    public DateTime? LastExitTime { get; set; }
}
