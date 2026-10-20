namespace ParkingGuidance.Web.Models;

/// <summary>Toạ độ khung bao quanh xe trên ảnh gốc (đơn vị: pixel).</summary>
public class BoundingBox
{
    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float X2 { get; set; }
    public float Y2 { get; set; }

    public float Width => X2 - X1;
    public float Height => Y2 - Y1;
}

/// <summary>Kết quả nhận diện 1 phương tiện trong 1 khung hình, kèm biển số (nếu đọc được).</summary>
public class DetectionResult
{
    /// <summary>Số thứ tự xe trong khung hình (dùng để hiển thị nhãn ①②③ trên ảnh).</summary>
    public int Index { get; set; }
    public VehicleType VehicleType { get; set; }
    public float Confidence { get; set; }
    public BoundingBox Box { get; set; } = new();
    public string? Color { get; set; }
    public string? PlateNumber { get; set; }
    public float? PlateConfidence { get; set; }

    /// <summary>Khung biển số trên ảnh gốc (pixel), null nếu không đọc được biển số.</summary>
    public BoundingBox? PlateBox { get; set; }
}

/// <summary>
/// Kết quả nhận diện 1 khung hình, tách rõ 2 bước: (1) có/không có xe, (2) biển số từng xe.
/// </summary>
public class FrameDetectionResponse
{
    /// <summary>Bước 1: khung hình có phát hiện được xe nào không.</summary>
    public bool HasVehicle { get; set; }
    public int VehicleCount { get; set; }

    /// <summary>Bước 2: danh sách xe kèm biển số (rỗng nếu HasVehicle = false).</summary>
    public List<DetectionResult> Vehicles { get; set; } = new();
}
