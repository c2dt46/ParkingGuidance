namespace ParkingGuidance.Web.Models;

/// <summary>Loại đối tượng có thể đặt lên bản đồ layout của trang Giám sát.</summary>
public enum LayoutElementType
{
    Button,
    Image,
    Text,
    ParkingSlot,

    /// <summary>Thống kê chỗ đỗ của tầng: số chỗ trống, số chỗ có xe, số lỗi không lấy được ảnh (cập nhật realtime).</summary>
    SlotStats,

    /// <summary>Hình chữ nhật (khung/vùng trang trí): tô nền, viền, bo góc, chữ bên trong, xoay.</summary>
    Rectangle,

    /// <summary>Hình tròn / elip: tô nền, viền, chữ bên trong, xoay. Có thể khoá để luôn tròn đều.</summary>
    Circle,

    /// <summary>Đường thẳng: chiều dài, độ dày, màu, kiểu nét (liền/đứt/chấm), mũi tên 2 đầu, góc xoay.</summary>
    Line,

    /// <summary>
    /// Đèn bit / Công tắc bit (kiểu Bit Lamp / Toggle Switch): hiển thị trạng thái 0/1 của 1 tag dạng bit
    /// (System tag hoặc Project tag kiểu Boolean) bằng hình ảnh theo trạng thái; ở chế độ Chạy, công tắc ghi giá trị về Project tag.
    /// </summary>
    BitLampSwitch
}

/// <summary>
/// 1 đối tượng được đặt lên bản đồ layout (nút ấn, hình ảnh, text, hoặc 1 lô đỗ xe).
///
/// X/Y/Width/Height tính theo <b>phần trăm (0-100)</b> so với khung mặt bằng (không phải
/// pixel tuyệt đối) — nhờ vậy vị trí các đối tượng luôn đúng tỉ lệ dù xem ở khung nhỏ (chế
/// độ chỉnh sửa) hay phóng to toàn màn hình (chế độ Chạy).
///
/// Với loại ParkingSlot: chỉ lưu tham chiếu (SlotId) tới 1 ParkingSlot quản lý bởi
/// IParkingService — trạng thái/biển số/thời điểm đỗ luôn lấy trực tiếp từ đó để đảm bảo
/// 1 nguồn dữ liệu duy nhất (không bị lệch giữa layout và dữ liệu thật).
/// </summary>
public class LayoutElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public LayoutElementType Type { get; set; }
    public string Floor { get; set; } = "B1";

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 16;
    public double Height { get; set; } = 10;

    // --- Nút ấn: chỉ hiển thị, chưa gắn hành động thật (đúng theo yêu cầu "để đó") ---
    public string? Label { get; set; }

    /// <summary>Chỉ dùng cho Nút ấn: mã mặt bằng sẽ chuyển tới khi bấm ở chế độ "Chạy" (null = không có hành động).</summary>
    public string? TargetFloorId { get; set; }

    // --- Hình ảnh ---
    public string? ImageUrl { get; set; }

    // --- Text ---
    public string? Content { get; set; }

    // --- Lô đỗ xe: tham chiếu tới ParkingSlot cùng SlotId ---
    public string? SlotId { get; set; }

    // --- Lô đỗ xe (vùng nhận diện): chọn thuộc tính nào được HIỂN THỊ trên bản đồ (chỉnh ở chế độ Build) ---
    public bool ShowStatusColor { get; set; } = true;     // màu nền/viền theo trạng thái: đỏ = có xe, vàng = lỗi, xanh = trống
    public bool ShowStatusText { get; set; } = true;      // chữ trạng thái: Lỗi / Đã đỗ / Còn trống
    public bool ShowPlate { get; set; } = true;           // biển số xe đang đỗ
    public bool ShowEntryTime { get; set; } = true;       // thời gian xe đó vào
    public bool ShowLastExitPlate { get; set; } = true;   // biển số xe gần nhất đã ra khỏi vị trí
    public bool ShowExitTime { get; set; } = true;        // thời gian ra

    // --- Thống kê chỗ đỗ (SlotStats): chọn con số nào được hiển thị ---
    public bool ShowStatFree { get; set; } = true;       // số chỗ trống
    public bool ShowStatOccupied { get; set; } = true;   // số chỗ có xe
    public bool ShowStatError { get; set; } = true;      // số lỗi không lấy được ảnh (camera mất kết nối)

    // --- Kiểu dáng chung cho MỌI loại đối tượng (null = không tuỳ chỉnh, dùng mặc định theo loại) ---
    public string? BackgroundColor { get; set; }
    public string? BorderStyle { get; set; }   // "solid" | "dashed" | "dotted" | "none"
    public string? BorderColor { get; set; }
    public double? BorderWidth { get; set; }

    // --- Thuộc tính mở rộng (dùng cho Hình chữ nhật / Hình tròn / Đường thẳng; null/0 = mặc định) ---

    /// <summary>Độ mờ CHUNG của cả đối tượng (0-100 %). null = 100.</summary>
    public double? Opacity { get; set; }

    /// <summary>Độ đậm của riêng màu nền (0-100 %). null = 100. Chỉ có tác dụng khi đặt BackgroundColor.</summary>
    public double? FillOpacity { get; set; }

    /// <summary>Góc xoay quanh tâm (độ, -360..360). null = 0.</summary>
    public double? Rotation { get; set; }

    /// <summary>Bo góc của hình chữ nhật, theo "px thiết kế" (phóng theo khung giống chữ/viền). null = vuông góc.</summary>
    public double? CornerRadius { get; set; }

    /// <summary>Thứ tự lớp: số lớn nằm trên. Mặc định 0; dùng số âm để đưa hình xuống dưới các ô đỗ.</summary>
    public int ZIndex { get; set; }

    /// <summary>Màu chữ bên trong hình (#rrggbb). null = màu mặc định.</summary>
    public string? TextColor { get; set; }

    /// <summary>Cỡ chữ bên trong hình, theo "px thiết kế". null = mặc định.</summary>
    public double? FontSize { get; set; }

    /// <summary>Đường thẳng: mũi tên ở đầu nào — "none" | "start" | "end" | "both". Màu/độ dày/kiểu nét dùng BorderColor/BorderWidth/BorderStyle.</summary>
    public string? LineArrow { get; set; }

    // --- Đèn bit / Công tắc bit (BitLampSwitch) ---

    /// <summary>Ghi chú của đối tượng (chỉ để người thiết kế dễ nhận biết, không hiển thị ở chế độ Chạy).</summary>
    public string? Comment { get; set; }

    /// <summary>"Lamp" = chỉ hiển thị trạng thái; "Switch" = hiển thị và bấm để ghi giá trị (chế độ Chạy).</summary>
    public string? BitMode { get; set; }

    /// <summary>Tag bit dùng để HIỂN THỊ trạng thái (System tag Boolean hoặc Project tag Boolean).</summary>
    public string? ReadTag { get; set; }

    /// <summary>true = ghi sang tag khác (<see cref="WriteTag"/>); false = ghi vào chính <see cref="ReadTag"/>. Tag ghi phải là Project tag (System tag chỉ đọc).</summary>
    public bool SeparateWriteTag { get; set; }

    public string? WriteTag { get; set; }

    /// <summary>Đảo tín hiệu: trạng thái hiển thị = NOT giá trị tag; khi ghi cũng đảo tương ứng.</summary>
    public bool InvertSignal { get; set; }

    /// <summary>Kiểu công tắc: "SetOn" | "SetOff" | "Toggle" | "Momentary" (nhấn giữ = ON, nhả = OFF).</summary>
    public string? SwitchStyle { get; set; }

    /// <summary>Chỉ ghi khi nhả nút chuột (không áp dụng cho Momentary).</summary>
    public bool WriteOnRelease { get; set; }

    /// <summary>Hình ảnh hiển thị khi trạng thái = 0 / = 1 (null = vẽ hình mặc định).</summary>
    public string? StateImage0 { get; set; }
    public string? StateImage1 { get; set; }

    /// <summary>Chữ hiển thị khi trạng thái = 0 / = 1 (tab Label).</summary>
    public string? StateText0 { get; set; }
    public string? StateText1 { get; set; }
}
