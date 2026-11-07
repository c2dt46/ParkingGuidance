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
    BitLampSwitch,

    /// <summary>
    /// Số (Number): hiển thị hoặc NHẬP giá trị của 1 biến dạng số — System tag / Project tag (Integer, Real), biến MS, biến MC.
    /// Số nguyên có thể "dời dấu chấm" (123 → 1.23). Chế độ nhập chỉ ghi được vào Project tag.
    /// </summary>
    Number,

    /// <summary>Ngày giờ (DateTime): đồng hồ hiển thị ngày / giờ hiện tại, chạy liên tục. Tuỳ chọn: chỉ ngày / chỉ giờ / cả hai, định dạng ngày,
    /// thứ trong tuần, 24h / 12h, giây, 1 dòng / 2 dòng, ngôn ngữ, múi giờ, mẫu tuỳ chọn (xem <see cref="DateTimeOptions"/>).</summary>
    DateTime
}

/// <summary>Tuỳ chọn hiển thị của đối tượng Ngày giờ (DateTime). Mọi giá trị đều được máy chủ kiểm tra / ép về miền hợp lệ (<see cref="Clean"/>).</summary>
public class DateTimeOptions
{
    /// <summary>"DateTime" (ngày + giờ) | "Date" (chỉ ngày) | "Time" (chỉ giờ).</summary>
    public string Show { get; set; } = "DateTime";

    /// <summary>Mẫu ngày, ký tự: yyyy yy MMMM MMM MM M dd d (vd "dd/MM/yyyy", "yyyy-MM-dd", "d MMMM, yyyy").</summary>
    public string DateFormat { get; set; } = "dd/MM/yyyy";

    /// <summary>"24h" | "12h" (SA/CH hoặc AM/PM).</summary>
    public string TimeFormat { get; set; } = "24h";

    public bool ShowSeconds { get; set; } = true;

    /// <summary>Thứ trong tuần đứng trước ngày: "none" | "long" (Thứ Tư) | "short" (T4).</summary>
    public string Weekday { get; set; } = "none";

    /// <summary>"OneLine" (1 dòng) | "TwoLines" (ngày trên, giờ dưới).</summary>
    public string Layout { get; set; } = "OneLine";

    /// <summary>true = giờ đứng trước ngày.</summary>
    public bool TimeFirst { get; set; }

    /// <summary>Chữ ngăn cách ngày và giờ khi hiển thị 1 dòng.</summary>
    public string Separator { get; set; } = "  ";

    /// <summary>"vi" | "en" (tên thứ, tháng, SA/CH - AM/PM).</summary>
    public string Language { get; set; } = "vi";

    /// <summary>"local" (giờ máy tính) | "utc" | độ lệch "+07:00" / "-05:00".</summary>
    public string TimeZone { get; set; } = "local";

    /// <summary>Mẫu tuỳ chọn nâng cao (vd "dddd, dd/MM/yyyy HH:mm:ss"). Có giá trị thì thay cho các lựa chọn ngày / giờ ở trên.</summary>
    public string? CustomPattern { get; set; }

    private static readonly System.Text.RegularExpressions.Regex TzRegex = new("^(local|utc|[+-](0[0-9]|1[0-4]):[0-5][0-9])$");

    private static string Pick(string? v, string def, params string[] allowed) => allowed.Contains(v) ? v! : def;

    private static string Trim(string? v, int max, string def)
    {
        if (v is null) return def;
        v = new string(v.Where(c => !char.IsControl(c)).ToArray());
        return v.Length > max ? v[..max] : v;
    }

    /// <summary>Bản đã kiểm tra: liệt kê giá trị hợp lệ, cắt độ dài, bỏ ký tự điều khiển. null → mặc định.</summary>
    public static DateTimeOptions Clean(DateTimeOptions? o)
    {
        o ??= new DateTimeOptions();
        var custom = Trim(o.CustomPattern, 80, "").Trim();
        return new DateTimeOptions
        {
            Show = Pick(o.Show, "DateTime", "DateTime", "Date", "Time"),
            DateFormat = Trim(o.DateFormat, 40, "dd/MM/yyyy") is { Length: > 0 } df ? df : "dd/MM/yyyy",
            TimeFormat = Pick(o.TimeFormat, "24h", "24h", "12h"),
            ShowSeconds = o.ShowSeconds,
            Weekday = Pick(o.Weekday, "none", "none", "long", "short"),
            Layout = Pick(o.Layout, "OneLine", "OneLine", "TwoLines"),
            TimeFirst = o.TimeFirst,
            Separator = Trim(o.Separator, 8, "  "),
            Language = Pick(o.Language, "vi", "vi", "en"),
            TimeZone = o.TimeZone is not null && TzRegex.IsMatch(o.TimeZone) ? o.TimeZone : "local",
            CustomPattern = custom.Length == 0 ? null : custom
        };
    }
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

    /// <summary>Nút ấn chuyển mặt bằng: tô màu nút khi mặt bằng ĐANG XEM chính là <see cref="TargetFloorId"/> để nhận biết (mặc định bật).</summary>
    public bool ButtonActiveEnabled { get; set; } = true;

    /// <summary>Nút ấn: false = chữ thường (mặc định là in đậm), null/true = in đậm.</summary>
    public bool? FontBold { get; set; }

    /// <summary>Nút ấn: true = chữ nghiêng.</summary>
    public bool? FontItalic { get; set; }

    /// <summary>Màu nền / màu chữ của nút khi đang ở mặt bằng đích (#rrggbb). null = mặc định (xanh lá / trắng).</summary>
    public string? ActiveBackgroundColor { get; set; }
    public string? ActiveTextColor { get; set; }

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

    /// <summary>SlotStats: hướng hiển thị các số liệu — "Vertical" (dọc, mặc định) | "Horizontal" (ngang).</summary>
    public string? StatsOrientation { get; set; }

    /// <summary>SlotStats: cỡ chữ của nhãn (Trống / Có xe / Lỗi), theo "px thiết kế". null = mặc định. Cỡ chữ SỐ lấy từ <see cref="FontSize"/>.</summary>
    public double? LabelFontSize { get; set; }

    /// <summary>SlotStats: số liệu có cộng phần đếm từ CAMERA (các vùng nhận diện đã vẽ của tầng) hay không (mặc định có).</summary>
    public bool StatFreeCamera { get; set; } = true;
    public bool StatOccupiedCamera { get; set; } = true;
    public bool StatErrorCamera { get; set; } = true;

    /// <summary>SlotStats: các biến MS (cảm biến) được CỘNG vào từng số liệu. Biến nên đếm đúng trạng thái của số liệu
    /// (Free → Còn trống, Occupied → Đã đỗ, Error → Lỗi). Số hiển thị = phần camera (nếu bật) + tổng giá trị các biến MS.</summary>
    public List<string> StatFreeVars { get; set; } = new();
    public List<string> StatOccupiedVars { get; set; } = new();
    public List<string> StatErrorVars { get; set; } = new();

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

    /// <summary>Biến dùng để HIỂN THỊ. Đèn/Công tắc bit: tag Boolean (System hoặc Project). Text: biến bất kỳ (String/char/số...). Number: biến số (System/Project Integer·Real, MS, MC).</summary>
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

    // --- Đèn (Lamp) hiển thị trạng thái CẢM BIẾN / VỊ TRÍ ĐỖ CHỈ ĐỊNH TRÊN CAMERA (thay cho tag bit) ---

    /// <summary>Nguồn dữ liệu của đèn: "Tag" (mặc định, tag bit 0/1) | "Sensor" (1 cảm biến MGT200/Keytop) | "CameraSlot" (1 vị trí đỗ đã vẽ trên camera).
    /// Chỉ áp dụng cho chế độ Lamp; Sensor/CameraSlot có 4 trạng thái Đã đỗ / Còn trống / Lỗi / Chưa sử dụng.</summary>
    public string? BitSource { get; set; }

    /// <summary>Nguồn Sensor: số thứ tự thiết bị bắt đầu từ 0 (ZCU 0.. của MGT200, hoặc Keytop 0..).</summary>
    public int SensorDevice { get; set; }

    /// <summary>Nguồn Sensor: chỉ số cảm biến trong thiết bị, bắt đầu từ 0 — MGT200 = (Line−1)×32 + ID (0–159); Keytop = cảm biến−1 (0–59).</summary>
    public int SensorIndex { get; set; }

    /// <summary>Nguồn CameraSlot: Zone và số thứ tự vị trí nhận diện (cùng tầng với đối tượng). Khoá ô = "{tầng}-{zone}-{số}".</summary>
    public string? LampZone { get; set; }
    public string? LampPosition { get; set; }

    /// <summary>Hình hiển thị theo 4 trạng thái của đèn Sensor/CameraSlot (null = vẽ chấm tròn mặc định theo màu).</summary>
    public string? StateImageParked { get; set; }    // Đã đỗ
    public string? StateImageFree { get; set; }      // Còn trống
    public string? StateImageError { get; set; }     // Lỗi
    public string? StateImageUnused { get; set; }    // Chưa sử dụng (chưa khai báo)

    /// <summary>Chữ hiển thị theo 4 trạng thái (tab Label) của đèn Sensor/CameraSlot.</summary>
    public string? StateTextParked { get; set; }
    public string? StateTextFree { get; set; }
    public string? StateTextError { get; set; }
    public string? StateTextUnused { get; set; }

    /// <summary>Chữ hiển thị khi trạng thái = 0 / = 1 (tab Label).</summary>
    public string? StateText0 { get; set; }
    public string? StateText1 { get; set; }

    // --- Text (gắn biến) / Number ---

    /// <summary>Text / Number: chữ đặt TRƯỚC giá trị (vd "Tầng B1: ").</summary>
    public string? Prefix { get; set; }

    /// <summary>Text / Number: chữ đặt SAU giá trị — thường là đơn vị (vd " °C", " xe").</summary>
    public string? Suffix { get; set; }

    /// <summary>Text / Number: căn lề — "left" | "center" | "right". null = mặc định theo loại.</summary>
    public string? TextAlign { get; set; }

    /// <summary>Number: "Display" = chỉ hiển thị; "Input" = cho phép nhập (ở chế độ Chạy) và ghi về Project tag.</summary>
    public string? ValueMode { get; set; }

    /// <summary>Number (biến số nguyên): dời dấu chấm đi N lần sang trái khi hiển thị (1234 với N=2 → 12.34); khi nhập thì nhân lại 10^N. 0..9.</summary>
    public int DecimalShift { get; set; }

    /// <summary>Number: số chữ số sau dấu chấm khi hiển thị. null = tự động (số nguyên: bằng DecimalShift; số thực: giữ nguyên).</summary>
    public int? Decimals { get; set; }

    /// <summary>Number (chế độ nhập): giá trị nhỏ nhất / lớn nhất cho phép nhập, tính theo giá trị HIỂN THỊ (sau khi dời dấu chấm). null = không giới hạn.</summary>
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }

    /// <summary>Ngày giờ (DateTime): tuỳ chọn hiển thị. Chữ trước / sau dùng <see cref="Prefix"/> / <see cref="Suffix"/>, căn lề dùng <see cref="TextAlign"/>.</summary>
    public DateTimeOptions? DtOptions { get; set; }

    /// <summary>Hiệu ứng động gắn biến (hiển thị / vị trí / màu sắc) — mọi loại đối tượng, chỉ chạy ở chế độ Chạy. null = không có.</summary>
    public ElementAnimation? Animation { get; set; }
}
