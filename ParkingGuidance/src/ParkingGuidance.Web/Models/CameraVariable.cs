namespace ParkingGuidance.Web.Models;

/// <summary>
/// Biến dữ liệu loại MC (liên quan tới camera). Giống biến MS nhưng đếm trên các VỊ TRÍ ĐỖ (vùng nhận diện khoanh trên ảnh camera)
/// được chỉ định: 1 biến chỉ đếm MỘT trạng thái — Giá trị = số vị trí (trong tập đó) đang ở trạng thái <see cref="Count"/>.
/// Lưu ở data/camera-variables.json.
/// </summary>
public class CameraVariable
{
    /// <summary>Tên biến do hệ thống tự đặt: MC0, MC1, MC2… (chỉ số nhỏ nhất còn trống). Không đổi được.</summary>
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Trạng thái mà biến đếm: Free = vị trí còn trống, Occupied = vị trí đã có xe đỗ, Error = vị trí lỗi
    /// (vd camera mất kết nối). null = chưa xác định (biến vừa thêm). Đã xác định thì KHÔNG đổi được nữa.</summary>
    public SensorCountState? Count { get; set; }

    /// <summary>Các vị trí đỗ được chỉ định, theo khoá ô "{tầng}-{zone}-{số}" (vd "B1-2-5").</summary>
    public List<string> Slots { get; set; } = new();

    public CameraVariable Clone() => new()
    {
        Name = Name, Description = Description, Count = Count,
        Slots = new List<string>(Slots ?? new())
    };
}
