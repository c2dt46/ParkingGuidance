namespace ParkingGuidance.Web.Models;

/// <summary>Toạ độ khung bao quanh xe trên ảnh gốc (đơn vị: pixel).</summary>
public class BoundingBox
{
    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float X2 { get; set; }
    public float Y2 { get; set; }

    /// <summary>Độ tin cậy của BỘ DÒ cho khung này (0 = không biết). Chỉ có với khung biển từ <c>DetectPlateBoxes</c>.</summary>
    public float Score { get; set; }

    /// <summary>Các khung biển ỨNG VIÊN khác của cùng xe (đã xếp hạng, không gồm khung này). <c>ReadPlateAt</c> đọc cả khung này lẫn các khung này
    /// rồi chọn vị trí đọc tốt nhất, thay vì chỉ đọc đúng 1 khung được chọn bằng hình học. Không đưa ra JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<BoundingBox>? Alternatives { get; set; }

    /// <summary>Màu nền biển số (white / yellow / blue / red / unknown) nhận từ ảnh tại khung biển được đọc. Chỉ có ở khung biển trả về sau khi đọc.</summary>
    public string? PlateColor { get; set; }
    /// <summary>Tỉ lệ điểm ảnh có màu rõ thuộc màu nền chủ đạo (0–1).</summary>
    public float PlateColorShare { get; set; }
    /// <summary>Loại cú pháp của chuỗi biển đọc được (Civilian / Military / Foreign / Special / StateAgency / Unknown = không khớp cú pháp nào).</summary>
    public string? PlateKind { get; set; }
    /// <summary>Tên LOẠI BIỂN (tiếng Việt) để hiển thị, vd "Biển kinh doanh vận tải (nền vàng)".</summary>
    public string? PlateLabel { get; set; }

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
