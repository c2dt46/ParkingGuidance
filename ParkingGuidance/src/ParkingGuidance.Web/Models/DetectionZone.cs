namespace ParkingGuidance.Web.Models;

/// <summary>1 đỉnh của vùng nhận diện, toạ độ tính theo % (0-100) so với ảnh camera.</summary>
public class ZonePoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>
/// 1 vùng nhận diện (đa giác) trên hình ảnh của camera, tương ứng với 1 ô đỗ cụ thể
/// (<see cref="SlotId"/>). Một xe chỉ được tính là "đang đỗ ở ô này" khi PHẦN LỚN diện tích
/// khung xe (bounding box) nằm trong vùng — xem <c>ZoneUtils.ComputeCoverageRatio</c>.
/// </summary>
public class DetectionZone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? SlotId { get; set; }
    public List<ZonePoint> Points { get; set; } = new();
}
