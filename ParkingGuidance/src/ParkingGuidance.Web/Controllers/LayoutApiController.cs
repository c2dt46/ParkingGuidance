using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.CameraRegistry;
using ParkingGuidance.Web.Services.Layout;
using ParkingGuidance.Web.Services.Parking;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Sensor;

namespace ParkingGuidance.Web.Controllers;

[ApiController]
[Route("api/layout")]
public class LayoutApiController : ControllerBase
{
    private readonly ILayoutService _layout;
    private readonly IParkingService _parking;
    private readonly ICameraRegistryService _registry;
    private readonly IWebHostEnvironment _env;
    private readonly IPersistenceService _persistence;
    private readonly ISensorService _sensors;
    private readonly ISensorVariableService _sensorVars;

    public LayoutApiController(ILayoutService layout, IParkingService parking, ICameraRegistryService registry, IWebHostEnvironment env, IPersistenceService persistence, ISensorService sensors, ISensorVariableService sensorVars)
    {
        _persistence = persistence;
        _sensors = sensors;
        _sensorVars = sensorVars;
        _layout = layout;
        _parking = parking;
        _registry = registry;
        _env = env;
    }

    public class ElementDto
    {
        public LayoutElementType Type { get; set; }
        public string Floor { get; set; } = "B1";

        // Vi tri + kich thuoc (don vi: phan tram 0-100 so voi khung mat bang)
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 16;
        public double Height { get; set; } = 10;

        public string? Label { get; set; }
        public string? TargetFloorId { get; set; }
        public bool ButtonActiveEnabled { get; set; } = true;
        public bool? FontBold { get; set; }
        public bool? FontItalic { get; set; }
        public string? ActiveBackgroundColor { get; set; }
        public string? ActiveTextColor { get; set; }
        public string? ImageUrl { get; set; }
        public string? Content { get; set; }
        /// <summary>Với loại ParkingSlot: SỐ THỨ TỰ vị trí nhận diện (1, 2, 3...) — khoá ô "{tầng}-{zone}-{số}" do máy chủ tạo.</summary>
        public string? SlotId { get; set; }

        /// <summary>Với loại ParkingSlot: Zone (khu vực lắp đặt trong tầng) chứa vị trí nhận diện — số tự nhiên nguyên dương.</summary>
        public string? Zone { get; set; }

        // Thuoc tinh vung nhan dien duoc hien thi tren ban do (che do Build)
        public bool ShowStatusColor { get; set; } = true;
        public bool ShowStatusText { get; set; } = true;
        public bool ShowPlate { get; set; } = true;
        public bool ShowEntryTime { get; set; } = true;
        public bool ShowLastExitPlate { get; set; } = true;
        public bool ShowExitTime { get; set; } = true;

        // Thong ke cho do: con so nao duoc hien thi
        public bool ShowStatFree { get; set; } = true;
        public bool ShowStatOccupied { get; set; } = true;
        public bool ShowStatError { get; set; } = true;

        // Kieu dang chung
        public string? BackgroundColor { get; set; }
        public string? BorderStyle { get; set; }
        public string? BorderColor { get; set; }
        public double? BorderWidth { get; set; }

        // Thuoc tinh mo rong cho Hinh chu nhat / Hinh tron / Duong thang
        public double? Opacity { get; set; }
        public double? FillOpacity { get; set; }
        public double? Rotation { get; set; }
        public double? CornerRadius { get; set; }
        public int ZIndex { get; set; }
        public string? TextColor { get; set; }
        public double? FontSize { get; set; }
        public string? LineArrow { get; set; }

        // Den bit / Cong tac bit
        public string? Comment { get; set; }
        public string? BitMode { get; set; }
        public string? ReadTag { get; set; }
        public bool SeparateWriteTag { get; set; }
        public string? WriteTag { get; set; }
        public bool InvertSignal { get; set; }
        public string? SwitchStyle { get; set; }
        public bool WriteOnRelease { get; set; }
        public string? StateImage0 { get; set; }
        public string? StateImage1 { get; set; }
        public string? StateText0 { get; set; }
        public string? StateText1 { get; set; }

        // Thong ke cho (SlotStats): huong hien thi, co chu nhan, cong them camera / bien MS (cam bien) vao tung so lieu
        public string? StatsOrientation { get; set; }
        public double? LabelFontSize { get; set; }
        public bool StatFreeCamera { get; set; } = true;
        public bool StatOccupiedCamera { get; set; } = true;
        public bool StatErrorCamera { get; set; } = true;
        public List<string>? StatFreeVars { get; set; }
        public List<string>? StatOccupiedVars { get; set; }
        public List<string>? StatErrorVars { get; set; }

        // Den (Lamp) hien thi trang thai cam bien / vi tri do tren camera
        public string? BitSource { get; set; }
        public int SensorDevice { get; set; }
        public int SensorIndex { get; set; }
        public string? LampZone { get; set; }
        public string? LampPosition { get; set; }
        public string? StateImageParked { get; set; }
        public string? StateImageFree { get; set; }
        public string? StateImageError { get; set; }
        public string? StateImageUnused { get; set; }
        public string? StateTextParked { get; set; }
        public string? StateTextFree { get; set; }
        public string? StateTextError { get; set; }
        public string? StateTextUnused { get; set; }

        // Text (gan bien) / Number
        public string? Prefix { get; set; }
        public string? Suffix { get; set; }
        public string? TextAlign { get; set; }
        public string? ValueMode { get; set; }
        public int DecimalShift { get; set; }
        public int? Decimals { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
    }

    private static double? ClampOrNull(double? v, double min, double max) =>
        v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value) ? null : Math.Clamp(v.Value, min, max);

    /// <summary>Gan cac thuoc tinh mo rong (da kiem tra/ep ve mien hop le) — dung chung cho Them va Cap nhat.</summary>
    private static void ApplyExtendedProps(LayoutElement e, ElementDto dto)
    {
        e.Opacity = ClampOrNull(dto.Opacity, 0, 100);
        e.FillOpacity = ClampOrNull(dto.FillOpacity, 0, 100);
        e.Rotation = ClampOrNull(dto.Rotation, -360, 360);
        e.CornerRadius = ClampOrNull(dto.CornerRadius, 0, 500);
        e.ZIndex = Math.Clamp(dto.ZIndex, -1000, 1000);
        e.TextColor = dto.TextColor is not null && Regex.IsMatch(dto.TextColor, "^#[0-9a-fA-F]{6}$") ? dto.TextColor : null;
        e.FontSize = ClampOrNull(dto.FontSize, 1, 200);
        e.LineArrow = dto.LineArrow is "none" or "start" or "end" or "both" ? dto.LineArrow : null;
    }

    /// <summary>Nút ấn: bật/tắt đổi màu khi đang ở mặt bằng đích + 2 màu tuỳ chọn (chỉ nhận #rrggbb). Loại khác: bỏ qua.</summary>
    private static void ApplyButtonProps(LayoutElement e, ElementDto dto)
    {
        if (e.Type != LayoutElementType.Button) return;
        static string? Hex(string? c) => c is not null && Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$") ? c : null;
        e.ButtonActiveEnabled = dto.ButtonActiveEnabled;
        e.TextAlign = dto.TextAlign is "left" or "center" or "right" ? dto.TextAlign : null;
        e.FontBold = dto.FontBold == false ? false : null;          // chi luu khi tat dam (mac dinh dam)
        e.FontItalic = dto.FontItalic == true ? true : null;
        e.ActiveBackgroundColor = Hex(dto.ActiveBackgroundColor);
        e.ActiveTextColor = Hex(dto.ActiveTextColor);
    }

    /// <summary>Thống kê chỗ (SlotStats): hướng, cỡ chữ nhãn, có tính camera không, danh sách biến MS cộng thêm (đã kiểm tra tên). Loại khác: bỏ qua.</summary>
    private static void ApplyStatProps(LayoutElement e, ElementDto dto)
    {
        if (e.Type != LayoutElementType.SlotStats) return;
        e.StatsOrientation = dto.StatsOrientation == "Horizontal" ? "Horizontal" : "Vertical";
        e.LabelFontSize = ClampOrNull(dto.LabelFontSize, 1, 200);
        e.StatFreeCamera = dto.StatFreeCamera;
        e.StatOccupiedCamera = dto.StatOccupiedCamera;
        e.StatErrorCamera = dto.StatErrorCamera;
        e.StatFreeVars = CleanVarList(dto.StatFreeVars);
        e.StatOccupiedVars = CleanVarList(dto.StatOccupiedVars);
        e.StatErrorVars = CleanVarList(dto.StatErrorVars);
    }

    private static List<string> CleanVarList(List<string>? names) =>
        (names ?? new List<string>())
            .Select(n => CleanTagName(n))
            .Where(n => n is not null).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

    private static readonly Regex TagNameRx = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

    private static string? CleanTagName(string? v) =>
        !string.IsNullOrWhiteSpace(v) && TagNameRx.IsMatch(v.Trim()) ? v.Trim() : null;

    private static string? CleanText(string? v, int max)
    {
        if (string.IsNullOrEmpty(v)) return null;
        v = v.Trim();
        return v.Length == 0 ? null : (v.Length > max ? v[..max] : v);
    }

    /// <summary>Chi cho anh trong thu muc /uploads/ cua may chu hoac duong dan http(s); tu choi javascript:, data:, ../ ...</summary>
    private static string? CleanImageUrl(string? v)
    {
        v = v?.Trim();
        if (string.IsNullOrEmpty(v) || v.Length > 500) return null;
        if (v.StartsWith("/uploads/", StringComparison.Ordinal) && !v.Contains("..") && !v.Contains('\\')) return v;
        return Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? v : null;
    }

    /// <summary>Gan thuoc tinh Den bit / Cong tac bit (da kiem tra). Loai doi tuong khac: de nguyen gia tri mac dinh.</summary>
    private static void ApplyBitProps(LayoutElement e, ElementDto dto)
    {
        if (e.Type != LayoutElementType.BitLampSwitch) return;
        e.Comment = CleanText(dto.Comment, 200);
        e.BitMode = dto.BitMode is "Lamp" or "Switch" ? dto.BitMode : "Switch";
        e.ReadTag = CleanTagName(dto.ReadTag);
        e.SeparateWriteTag = dto.SeparateWriteTag;
        e.WriteTag = dto.SeparateWriteTag ? CleanTagName(dto.WriteTag) : null;
        e.InvertSignal = dto.InvertSignal;
        e.SwitchStyle = dto.SwitchStyle is "SetOn" or "SetOff" or "Toggle" or "Momentary" ? dto.SwitchStyle : "Toggle";
        e.WriteOnRelease = dto.WriteOnRelease && e.SwitchStyle != "Momentary";
        e.StateImage0 = CleanImageUrl(dto.StateImage0);
        e.StateImage1 = CleanImageUrl(dto.StateImage1);
        e.StateText0 = CleanText(dto.StateText0, 64);
        e.StateText1 = CleanText(dto.StateText1, 64);

        // Nguon Cam bien / Vi tri do tren camera: chi hien thi (Lamp), khong co tag ghi
        e.BitSource = dto.BitSource is "Sensor" or "CameraSlot" ? dto.BitSource : null;
        if (e.BitSource is not null)
        {
            e.BitMode = "Lamp";
            e.SeparateWriteTag = false;
            e.WriteTag = null;
            e.WriteOnRelease = false;
        }
        e.SensorDevice = Math.Clamp(dto.SensorDevice, 0, SensorSettings.KeytopMax - 1);
        e.SensorIndex = Math.Clamp(dto.SensorIndex, 0, SensorSettings.Pm200SensorsPerZcu - 1);
        e.LampZone = SlotKey.TryNormalizeNumber(dto.LampZone, out var lz) ? lz : null;
        e.LampPosition = SlotKey.TryNormalizeNumber(dto.LampPosition, out var lp) ? lp : null;
        e.StateImageParked = CleanImageUrl(dto.StateImageParked);
        e.StateImageFree = CleanImageUrl(dto.StateImageFree);
        e.StateImageError = CleanImageUrl(dto.StateImageError);
        e.StateImageUnused = CleanImageUrl(dto.StateImageUnused);
        e.StateTextParked = CleanText(dto.StateTextParked, 64);
        e.StateTextFree = CleanText(dto.StateTextFree, 64);
        e.StateTextError = CleanText(dto.StateTextError, 64);
        e.StateTextUnused = CleanText(dto.StateTextUnused, 64);
    }

    /// <summary>Text (gắn biến) và Number: biến hiển thị/ghi, định dạng số (đã kiểm tra). Loại khác: bỏ qua.</summary>
    private static void ApplyValueProps(LayoutElement e, ElementDto dto)
    {
        if (e.Type is not (LayoutElementType.Text or LayoutElementType.Number)) return;
        e.Comment = CleanText(dto.Comment, 200);
        e.ReadTag = CleanTagName(dto.ReadTag);
        e.Prefix = string.IsNullOrEmpty(dto.Prefix) ? null : (dto.Prefix.Length > 32 ? dto.Prefix[..32] : dto.Prefix);
        e.Suffix = string.IsNullOrEmpty(dto.Suffix) ? null : (dto.Suffix.Length > 32 ? dto.Suffix[..32] : dto.Suffix);
        e.TextAlign = dto.TextAlign is "left" or "center" or "right" ? dto.TextAlign : null;
        if (e.Type != LayoutElementType.Number) return;

        e.ValueMode = dto.ValueMode == "Input" ? "Input" : "Display";
        e.DecimalShift = Math.Clamp(dto.DecimalShift, 0, 9);
        e.Decimals = dto.Decimals is null ? null : Math.Clamp(dto.Decimals.Value, 0, 9);
        e.MinValue = ClampOrNull(dto.MinValue, -1e15, 1e15);
        e.MaxValue = ClampOrNull(dto.MaxValue, -1e15, 1e15);
        e.SeparateWriteTag = e.ValueMode == "Input" && dto.SeparateWriteTag;
        e.WriteTag = e.SeparateWriteTag ? CleanTagName(dto.WriteTag) : null;
    }

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    /// <summary>Tai len 1 hinh (vd hinh den/cong tac cho tung trang thai) vao {thu muc du an}/uploads/layout/ (phuc vu o duong dan /uploads/layout/...). Khong nhan SVG (tranh script khi mo truc tiep).</summary>
    [HttpPost("upload-image")]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> UploadImage(IFormFile image)
    {
        if (image is null || image.Length == 0)
            return BadRequest(new { message = "Thieu file anh." });
        if (image.Length > 5_000_000)
            return BadRequest(new { message = "Anh toi da 5 MB." });

        var ext = Path.GetExtension(image.FileName ?? "");
        if (string.IsNullOrWhiteSpace(ext) || !ImageExts.Contains(ext))
            return BadRequest(new { message = "Chi nhan anh .png, .jpg, .jpeg, .gif, .webp, .bmp." });

        var dir = Path.Combine(_persistence.ProjectUploadsDirectory, "layout");
        Directory.CreateDirectory(dir);
        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";

        await using (var stream = System.IO.File.Create(Path.Combine(dir, fileName)))
        {
            await image.CopyToAsync(stream);
        }
        return Ok(new { url = $"/uploads/layout/{fileName}" });
    }

    /// <summary>
    /// Danh sách VỊ TRÍ NHẬN DIỆN đã vẽ của các camera trên tầng này, kèm Zone (mỗi vị trí = 1 ô đỗ, số thứ tự không trùng trong cùng Zone) —
    /// dùng cho công cụ "Thêm vùng nhận diện" của Layout Editor. <c>onLayout</c> = vùng đã có trên bản đồ tầng này.
    /// </summary>
    [HttpGet("zones")]
    public IActionResult GetZones([FromQuery] string floor = "B1")
    {
        var floorKey = SlotKey.NormalizeFloor(floor);

        // Khoa o (tang-so) cua cac vung da dat tren ban do tang nay
        var placed = _layout.GetElements(floor)
            .Where(e => e.Type == LayoutElementType.ParkingSlot && !string.IsNullOrWhiteSpace(e.SlotId))
            .Select(e => e.SlotId!.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        var zones = _registry.GetAll()
            .Where(c => SlotKey.NormalizeFloor(c.Floor) == floorKey)
            .SelectMany(c => c.Zones
                .Where(z => SlotKey.TryNormalizeNumber(z.SlotId, out _))
                .Select(z =>
                {
                    SlotKey.TryNormalizeNumber(z.SlotId, out var number);
                    var areaNumber = SlotKey.NormalizeZone(c.Zone);
                    return new
                    {
                        Zone = areaNumber, ZoneValue = int.Parse(areaNumber),
                        Number = number, Value = int.Parse(number),
                        CameraId = c.Id, CameraIp = c.IpAddress
                    };
                }))
            .GroupBy(z => (z.Zone, z.Number))
            .Select(g => g.First())
            .OrderBy(z => z.ZoneValue).ThenBy(z => z.Value)
            .Select(z => new
            {
                z.Zone,
                z.Number,
                z.CameraId,
                z.CameraIp,
                OnLayout = placed.Contains(SlotKey.Make(floor, z.Zone, z.Number))
            })
            .ToList();

        return Ok(zones);
    }

    public class SlotDisplayDto
    {
        public bool? ShowStatusColor { get; set; }
        public bool? ShowStatusText { get; set; }
        public bool? ShowPlate { get; set; }
        public bool? ShowEntryTime { get; set; }
        public bool? ShowLastExitPlate { get; set; }
        public bool? ShowExitTime { get; set; }
    }

    /// <summary>Đặt hiển thị/ẩn thuộc tính cho TẤT CẢ vùng nhận diện đang có trên bản đồ của tầng (chỉ áp các trường được gửi).</summary>
    [HttpPut("slot-display")]
    public IActionResult UpdateAllSlotDisplay([FromQuery] string floor, [FromBody] SlotDisplayDto dto)
    {
        int n = 0;
        foreach (var el in _layout.GetElements(floor).Where(e => e.Type == LayoutElementType.ParkingSlot))
        {
            _layout.UpdateElement(el.Id, e =>
            {
                if (dto.ShowStatusColor is { } a) e.ShowStatusColor = a;
                if (dto.ShowStatusText is { } b) e.ShowStatusText = b;
                if (dto.ShowPlate is { } c) e.ShowPlate = c;
                if (dto.ShowEntryTime is { } d) e.ShowEntryTime = d;
                if (dto.ShowLastExitPlate is { } f) e.ShowLastExitPlate = f;
                if (dto.ShowExitTime is { } g) e.ShowExitTime = g;
            });
            n++;
        }
        return Ok(new { updated = n });
    }

    /// <summary>
    /// Thống kê chỗ đỗ của 1 tầng — tính trên các VÙNG NHẬN DIỆN đang có của camera trên tầng (ô của vùng đã xoá không được tính):
    /// <c>free</c> = Còn trống, <c>occupied</c> = Đã đỗ, <c>error</c> = ô đang Lỗi (camera không lấy được ảnh),
    /// <c>camerasError</c> = số camera đang không lấy được ảnh.
    /// </summary>
    private readonly record struct CameraCount(int Total, int Free, int Occupied, int Error, int CamerasError);

    private CameraCount CountCameras(string floor)
    {
        var floorKey = SlotKey.NormalizeFloor(floor);
        var slotByKey = _parking.GetSlots(null)
            .Where(sl => SlotKey.NormalizeFloor(sl.Floor) == floorKey)
            .ToDictionary(sl => sl.SlotId, StringComparer.Ordinal);

        var cams = _registry.GetAll().Where(c => SlotKey.NormalizeFloor(c.Floor) == floorKey).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errorCameras = new HashSet<string>();
        int free = 0, occupied = 0, error = 0;

        foreach (var cam in cams)
        {
            foreach (var zone in cam.Zones)
            {
                var key = SlotKey.ForZone(cam.Floor, cam.Zone, zone.SlotId);
                if (key is null || !seen.Add(key)) continue;

                var status = slotByKey.TryGetValue(key, out var slot) ? slot.Status : SlotStatus.Empty;
                switch (status)
                {
                    case SlotStatus.Occupied: occupied++; break;
                    case SlotStatus.Error: error++; errorCameras.Add(cam.Id); break;
                    default: free++; break;
                }
            }
        }
        return new CameraCount(seen.Count, free, occupied, error, errorCameras.Count);
    }

    /// <summary>
    /// Thống kê chỗ đỗ của 1 tầng. Phần CAMERA tính trên các VÙNG NHẬN DIỆN đang có của camera trên tầng (ô của vùng đã xoá không được tính):
    /// free = Còn trống, occupied = Đã đỗ, error = ô đang Lỗi (camera không lấy được ảnh), camerasError = số camera đang không lấy được ảnh.
    /// Với 1 đối tượng Thống kê (<paramref name="el"/>): mỗi số liệu = phần camera (nếu đối tượng bật "tính từ camera") + TỔNG giá trị các biến MS
    /// (cảm biến) đã thêm cho số liệu đó. <c>sensorFree/sensorOccupied/sensorError</c> là phần cảm biến, <c>cameraFree/…</c> là phần camera.
    /// </summary>
    private object ComputeStats(string floor, LayoutElement? el = null, CameraCount? cam = null)
    {
        var c = cam ?? CountCameras(floor);

        IReadOnlyDictionary<string, long?>? ms = null;
        if (el is not null)
        {
            var names = el.StatFreeVars.Concat(el.StatOccupiedVars).Concat(el.StatErrorVars).ToList();
            if (names.Count > 0) ms = _sensorVars.GetValues(names);
        }
        long Sum(IEnumerable<string>? list) =>
            ms is null || list is null ? 0 : list.Distinct(StringComparer.OrdinalIgnoreCase).Sum(n => ms.TryGetValue(n, out var v) && v is long x ? x : 0);

        int sf = (int)Sum(el?.StatFreeVars), so = (int)Sum(el?.StatOccupiedVars), se = (int)Sum(el?.StatErrorVars);
        bool cf = el?.StatFreeCamera ?? true, co = el?.StatOccupiedCamera ?? true, ce = el?.StatErrorCamera ?? true;

        return new
        {
            Total = c.Total,
            Free = (cf ? c.Free : 0) + sf,
            Occupied = (co ? c.Occupied : 0) + so,
            Error = (ce ? c.Error : 0) + se,
            CamerasError = c.CamerasError,
            CameraFree = c.Free, CameraOccupied = c.Occupied, CameraError = c.Error,
            SensorFree = sf, SensorOccupied = so, SensorError = se
        };
    }

    [HttpGet("stats")]
    public IActionResult GetStats([FromQuery] string floor = "B1") => Ok(ComputeStats(floor));

    // ---- Trạng thái đèn nguồn Cảm biến / Vị trí đỗ trên camera (dùng chung cho lamp-states và lamp-info) ----

    /// <summary>Cảm biến: thiết bị không kích hoạt / ngoài số thiết bị đang dùng / chưa báo trạng thái → Unused; thiết bị mất kết nối → Error;
    /// còn lại 0 = Free, 1 = Parked, 2 = Error. Kèm lý do (hiện ở cửa sổ thông tin).</summary>
    private static (string State, string Reason) SensorLampState(SensorSnapshot? snap, LayoutElement e)
    {
        if (snap is null || e.SensorDevice < 0 || e.SensorDevice >= snap.Devices.Count)
            return ("Unused", "Thiết bị nằm ngoài số thiết bị đang sử dụng");
        var d = snap.Devices[e.SensorDevice];
        if (!d.Enabled) return ("Unused", "Thiết bị chưa được kích hoạt");
        if (e.SensorIndex < 0 || e.SensorIndex >= snap.SensorsPerDevice)
            return ("Unused", "Cảm biến nằm ngoài số cảm biến của thiết bị");
        if (!d.Online) return ("Error", "Thiết bị đang mất kết nối");
        var c = e.SensorIndex < d.States.Length ? d.States[e.SensorIndex] : '?';
        return c switch
        {
            '0' => ("Free", ""),
            '1' => ("Parked", ""),
            '2' => ("Error", "Cảm biến báo lỗi"),
            _ => ("Unused", "Cảm biến chưa báo trạng thái lần nào")
        };
    }

    /// <summary>Vị trí đỗ trên camera: chưa khai báo trên camera nào của tầng+Zone → Unused; còn lại theo ô đỗ (Occupied = Parked, Error, còn lại Free).</summary>
    private static (string State, string Reason) CameraLampState(LayoutElement e, HashSet<string> declared, Dictionary<string, ParkingSlot> slotByKey)
    {
        var key = SlotKey.ForZone(e.Floor, e.LampZone, e.LampPosition);
        if (key is null) return ("Unused", "Chưa chọn vị trí đỗ");
        if (!declared.Contains(key)) return ("Unused", "Vị trí chưa được khai báo trên camera nào của tầng");
        var status = slotByKey.TryGetValue(key, out var slot) ? slot.Status : SlotStatus.Empty;
        return status switch
        {
            SlotStatus.Occupied => ("Parked", ""),
            SlotStatus.Error => ("Error", slot?.ErrorMessage ?? "Camera không lấy được ảnh"),
            _ => ("Free", "")
        };
    }

    /// <summary>Các vị trí đã khai báo trên camera của tầng + ô đỗ hiện tại (khoá ô → ParkingSlot).</summary>
    private (HashSet<string> Declared, Dictionary<string, ParkingSlot> SlotByKey) CameraLookups(string floor)
    {
        var floorKey = SlotKey.NormalizeFloor(floor);
        var declared = _registry.GetAll()
            .Where(c => SlotKey.NormalizeFloor(c.Floor) == floorKey)
            .SelectMany(c => c.Zones.Select(z => SlotKey.ForZone(c.Floor, c.Zone, z.SlotId)))
            .Where(k => k is not null).Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);
        var slotByKey = _parking.GetSlots(floor).GroupBy(s => s.SlotId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return (declared, slotByKey);
    }

    /// <summary>
    /// Trạng thái sống của các ĐÈN (Lamp) trên tầng có nguồn là Cảm biến hoặc Vị trí đỗ chỉ định trên camera.
    /// Trả về { idĐối tượng: "Parked" | "Free" | "Error" | "Unused" } (xem <see cref="SensorLampState"/>, <see cref="CameraLampState"/>).
    /// </summary>
    [HttpGet("lamp-states")]
    public IActionResult GetLampStates([FromQuery] string floor = "B1")
    {
        var lamps = _layout.GetElements(floor)
            .Where(e => e.Type == LayoutElementType.BitLampSwitch && e.BitSource is "Sensor" or "CameraSlot")
            .ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (lamps.Count == 0) return Ok(result);

        SensorSnapshot? snap = lamps.Any(l => l.BitSource == "Sensor") ? _sensors.GetSnapshot() : null;
        var (declared, slotByKey) = lamps.Any(l => l.BitSource == "CameraSlot")
            ? CameraLookups(floor)
            : (new HashSet<string>(), new Dictionary<string, ParkingSlot>());

        foreach (var lamp in lamps)
            result[lamp.Id] = (lamp.BitSource == "Sensor" ? SensorLampState(snap, lamp) : CameraLampState(lamp, declared, slotByKey)).State;
        return Ok(result);
    }

    /// <summary>
    /// Thông tin chi tiết của 1 đèn nguồn Cảm biến / Vị trí đỗ trên camera — hiện khi bấm vào đèn ở chế độ Chạy.
    /// Cảm biến: loại + số thiết bị, Line/ID (MGT200) hoặc số cảm biến (Keytop), thiết bị kích hoạt / kết nối, lần cuối nhận dữ liệu.
    /// Camera: Zone + số vị trí, trạng thái ô, biển số đang đỗ / biển số chỉ định + kết quả đối chiếu, giờ vào, camera phụ trách, xe rời gần nhất, lỗi.
    /// </summary>
    [HttpGet("lamp-info/{id}")]
    public IActionResult GetLampInfo(string id, [FromQuery] string floor = "B1")
    {
        var el = _layout.GetElements(floor).FirstOrDefault(e => e.Id == id && e.Type == LayoutElementType.BitLampSwitch);
        if (el is null || el.BitSource is not ("Sensor" or "CameraSlot"))
            return NotFound(new { message = "Không phải đèn cảm biến / vị trí đỗ." });

        if (el.BitSource == "Sensor")
        {
            var snap = _sensors.GetSnapshot();
            var (state, reason) = SensorLampState(snap, el);
            var dev = el.SensorDevice >= 0 && el.SensorDevice < snap.Devices.Count ? snap.Devices[el.SensorDevice] : null;
            bool keytop = string.Equals(snap.Type, "Keytop", StringComparison.OrdinalIgnoreCase);
            return Ok(new
            {
                Kind = "Sensor",
                State = state,
                Reason = reason,
                Comment = el.Comment,
                Sensor = new
                {
                    Type = snap.Type,
                    Device = el.SensorDevice,
                    Line = keytop ? (int?)null : el.SensorIndex / 32 + 1,
                    Id = keytop ? (int?)null : el.SensorIndex % 32,
                    Number = el.SensorIndex + 1,
                    DeviceEnabled = dev?.Enabled ?? false,
                    DeviceOnline = dev?.Online ?? false,
                    LastSeen = dev?.LastSeen
                }
            });
        }

        var (declared, slotByKey) = CameraLookups(floor);
        var (st, why) = CameraLampState(el, declared, slotByKey);
        var key = SlotKey.ForZone(el.Floor, el.LampZone, el.LampPosition);
        ParkingSlot? slot = key is not null && slotByKey.TryGetValue(key, out var sl) ? sl : null;

        var floorKey = SlotKey.NormalizeFloor(floor);
        var cameras = key is null ? new List<object>() : _registry.GetAll()
            .Where(c => SlotKey.NormalizeFloor(c.Floor) == floorKey && c.Zones.Any(z => SlotKey.ForZone(c.Floor, c.Zone, z.SlotId) == key))
            .Select(c => (object)new { Id = c.Id, Ip = c.IpAddress, Brand = c.Brand, Model = c.Model })
            .ToList();

        return Ok(new
        {
            Kind = "CameraSlot",
            State = st,
            Reason = why,
            Comment = el.Comment,
            Slot = new
            {
                Floor = floor,
                Zone = el.LampZone,
                Position = el.LampPosition,
                Declared = key is not null && declared.Contains(key),
                Status = slot?.Status.ToString(),
                PlateNumber = slot?.PlateNumber,
                AssignedPlate = slot?.AssignedPlate,
                PlateCheck = slot?.PlateCheck,
                ParkedAt = slot?.ParkedAt,
                ErrorMessage = slot?.ErrorMessage,
                LastExitPlate = slot?.LastExitPlate,
                LastExitTime = slot?.LastExitTime,
                Cameras = cameras
            }
        });
    }

    /// <summary>Lấy toàn bộ đối tượng layout của 1 tầng, kèm dữ liệu trạng thái sống (live)
    /// của ô đỗ (lấy từ IParkingService) cho các đối tượng loại ParkingSlot.</summary>
    [HttpGet]
    public IActionResult Get([FromQuery] string floor = "B1")
    {
        var elements = _layout.GetElements(floor);
        var slotsById = _parking.GetSlots(floor).ToDictionary(s => s.SlotId);
        CameraCount? camCount = elements.Any(e => e.Type == LayoutElementType.SlotStats) ? CountCameras(floor) : null;

        var result = elements.Select(e => new
        {
            e.Id,
            e.Type,
            e.Floor,
            e.X,
            e.Y,
            e.Width,
            e.Height,
            e.Label,
            e.TargetFloorId,
            e.ButtonActiveEnabled,
            e.FontBold,
            e.FontItalic,
            e.ActiveBackgroundColor,
            e.ActiveTextColor,
            e.ImageUrl,
            e.Content,
            e.SlotId,
            e.ShowStatusColor,
            e.ShowStatusText,
            e.ShowPlate,
            e.ShowEntryTime,
            e.ShowLastExitPlate,
            e.ShowExitTime,
            e.ShowStatFree,
            e.ShowStatOccupied,
            e.ShowStatError,
            e.StatsOrientation,
            e.LabelFontSize,
            e.StatFreeCamera,
            e.StatOccupiedCamera,
            e.StatErrorCamera,
            e.StatFreeVars,
            e.StatOccupiedVars,
            e.StatErrorVars,
            e.BackgroundColor,
            e.BorderStyle,
            e.BorderColor,
            e.BorderWidth,
            e.Opacity,
            e.FillOpacity,
            e.Rotation,
            e.CornerRadius,
            e.ZIndex,
            e.TextColor,
            e.FontSize,
            e.LineArrow,
            e.Comment,
            e.BitMode,
            e.ReadTag,
            e.SeparateWriteTag,
            e.WriteTag,
            e.InvertSignal,
            e.SwitchStyle,
            e.WriteOnRelease,
            e.StateImage0,
            e.StateImage1,
            e.StateText0,
            e.StateText1,
            e.BitSource,
            e.SensorDevice,
            e.SensorIndex,
            e.LampZone,
            e.LampPosition,
            e.StateImageParked,
            e.StateImageFree,
            e.StateImageError,
            e.StateImageUnused,
            e.StateTextParked,
            e.StateTextFree,
            e.StateTextError,
            e.StateTextUnused,
            e.Prefix,
            e.Suffix,
            e.TextAlign,
            e.ValueMode,
            e.DecimalShift,
            e.Decimals,
            e.MinValue,
            e.MaxValue,
            Stats = e.Type == LayoutElementType.SlotStats ? ComputeStats(floor, e, camCount) : null,
            Slot = e.Type == LayoutElementType.ParkingSlot && e.SlotId is not null &&
                   slotsById.TryGetValue(e.SlotId, out var slot)
                ? new
                {
                    slot.SlotId,
                    Zone = SlotKey.ZoneOf(slot.SlotId, slot.Zone),
                    Number = string.IsNullOrEmpty(slot.Number) ? SlotKey.NumberOfKey(slot.SlotId) : slot.Number,
                    slot.Status,
                    slot.PlateNumber,
                    slot.ParkedAt,
                    slot.ErrorMessage,
                    slot.LastExitPlate,
                    slot.LastExitTime,
                    slot.AssignedPlate,
                    slot.PlateCheck
                }
                : null
        });

        return Ok(result);
    }

    /// <summary>Thêm 1 đối tượng mới vào layout. Với loại ParkingSlot, tự tạo ô đỗ tương ứng
    /// trong IParkingService nếu SlotId chưa tồn tại.</summary>
    [HttpPost]
    public IActionResult Add([FromBody] ElementDto dto)
    {
        if (dto.Type == LayoutElementType.ParkingSlot)
        {
            // Giao dien gui SO THU TU vi tri (1, 2, 3...) va ZONE; khoa o "{tang}-{zone}-{so}" do may chu tao
            if (!SlotKey.TryNormalizeNumber(dto.SlotId, out var number))
                return BadRequest(new { message = $"{SlotKey.InvalidMessage} Gia tri khong hop le: '{dto.SlotId}'." });
            if (!SlotKey.TryNormalizeNumber(dto.Zone, out var areaNumber))
                return BadRequest(new { message = $"{SlotKey.ZoneInvalidMessage} Gia tri khong hop le: '{dto.Zone}'." });

            // Chi them duoc VI TRI NHAN DIEN da ve cho 1 camera thuoc tang + Zone nay (khong go tay ma tuy y)
            var floorKey = SlotKey.NormalizeFloor(dto.Floor);
            var zoneExists = _registry.GetAll().Any(c =>
                SlotKey.NormalizeFloor(c.Floor) == floorKey &&
                SlotKey.NormalizeZone(c.Zone) == areaNumber &&
                c.Zones.Any(z => SlotKey.TryNormalizeNumber(z.SlotId, out var zn) && zn == number));
            if (!zoneExists)
                return BadRequest(new { message = $"Tang {dto.Floor}, Zone {areaNumber} chua co vi tri nhan dien so {number}. " +
                                                  "Hay ve vi tri o He thong > Camera > Vi tri do truoc." });

            dto.SlotId = SlotKey.Make(dto.Floor, areaNumber, number);
            dto.Zone = areaNumber;

            // 1 vi tri chi dat 1 lan tren ban do cua tang
            if (_layout.GetElements(dto.Floor).Any(e => e.Type == LayoutElementType.ParkingSlot &&
                    string.Equals(e.SlotId, dto.SlotId, StringComparison.OrdinalIgnoreCase)))
                return Conflict(new { message = $"Vi tri so {number} (Zone {areaNumber}) da co tren ban do cua tang {dto.Floor}." });
        }

        var element = new LayoutElement
        {
            Type = dto.Type,
            Floor = dto.Floor,
            X = dto.X,
            Y = dto.Y,
            Width = dto.Width,
            Height = dto.Height,
            Label = dto.Label,
            TargetFloorId = dto.TargetFloorId,
            ImageUrl = dto.ImageUrl,
            Content = dto.Content,
            SlotId = dto.SlotId,
            ShowStatusColor = dto.ShowStatusColor,
            ShowStatusText = dto.ShowStatusText,
            ShowPlate = dto.ShowPlate,
            ShowEntryTime = dto.ShowEntryTime,
            ShowLastExitPlate = dto.ShowLastExitPlate,
            ShowExitTime = dto.ShowExitTime,
            ShowStatFree = dto.ShowStatFree,
            ShowStatOccupied = dto.ShowStatOccupied,
            ShowStatError = dto.ShowStatError,
            BackgroundColor = dto.BackgroundColor,
            BorderStyle = dto.BorderStyle,
            BorderColor = dto.BorderColor,
            BorderWidth = dto.BorderWidth
        };
        ApplyExtendedProps(element, dto);
        ApplyBitProps(element, dto);
        ApplyValueProps(element, dto);
        ApplyStatProps(element, dto);
        ApplyButtonProps(element, dto);

        if (element.Type == LayoutElementType.ParkingSlot)
            _parking.EnsureSlot(dto.SlotId!, dto.Floor, dto.Zone);

        _layout.AddElement(element);
        return Ok(element);
    }

    /// <summary>Cập nhật vị trí/kích thước/nội dung/kiểu dáng 1 đối tượng layout.</summary>
    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] ElementDto dto)
    {
        var updated = _layout.UpdateElement(id, e =>
        {
            e.X = dto.X;
            e.Y = dto.Y;
            e.Width = dto.Width;
            e.Height = dto.Height;
            e.Label = dto.Label;
            e.TargetFloorId = dto.TargetFloorId;
            e.ImageUrl = dto.ImageUrl;
            e.Content = dto.Content;
            e.ShowStatusColor = dto.ShowStatusColor;
            e.ShowStatusText = dto.ShowStatusText;
            e.ShowPlate = dto.ShowPlate;
            e.ShowEntryTime = dto.ShowEntryTime;
            e.ShowLastExitPlate = dto.ShowLastExitPlate;
            e.ShowExitTime = dto.ShowExitTime;
            e.ShowStatFree = dto.ShowStatFree;
            e.ShowStatOccupied = dto.ShowStatOccupied;
            e.ShowStatError = dto.ShowStatError;
            e.BackgroundColor = dto.BackgroundColor;
            e.BorderStyle = dto.BorderStyle;
            e.BorderColor = dto.BorderColor;
            e.BorderWidth = dto.BorderWidth;
            ApplyExtendedProps(e, dto);
            ApplyBitProps(e, dto);
            ApplyValueProps(e, dto);
            ApplyStatProps(e, dto);
            ApplyButtonProps(e, dto);
        });

        return updated is null
            ? NotFound(new { message = "Khong tim thay doi tuong layout." })
            : Ok(updated);
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id) =>
        _layout.DeleteElement(id)
            ? NoContent()
            : NotFound(new { message = "Khong tim thay doi tuong layout." });
}
