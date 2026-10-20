using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.CameraRegistry;
using ParkingGuidance.Web.Services.Layout;
using ParkingGuidance.Web.Services.Parking;

namespace ParkingGuidance.Web.Controllers;

[ApiController]
[Route("api/layout")]
public class LayoutApiController : ControllerBase
{
    private readonly ILayoutService _layout;
    private readonly IParkingService _parking;
    private readonly ICameraRegistryService _registry;
    private readonly IWebHostEnvironment _env;

    public LayoutApiController(ILayoutService layout, IParkingService parking, ICameraRegistryService registry, IWebHostEnvironment env)
    {
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
    }

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    /// <summary>Tai len 1 hinh (vd hinh den/cong tac cho tung trang thai) vao wwwroot/uploads/layout/. Khong nhan SVG (tranh script khi mo truc tiep).</summary>
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

        var dir = Path.Combine(_env.WebRootPath, "uploads", "layout");
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
    private object ComputeStats(string floor)
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

        return new { Total = seen.Count, Free = free, Occupied = occupied, Error = error, CamerasError = errorCameras.Count };
    }

    [HttpGet("stats")]
    public IActionResult GetStats([FromQuery] string floor = "B1") => Ok(ComputeStats(floor));

    /// <summary>Lấy toàn bộ đối tượng layout của 1 tầng, kèm dữ liệu trạng thái sống (live)
    /// của ô đỗ (lấy từ IParkingService) cho các đối tượng loại ParkingSlot.</summary>
    [HttpGet]
    public IActionResult Get([FromQuery] string floor = "B1")
    {
        var elements = _layout.GetElements(floor);
        var slotsById = _parking.GetSlots(floor).ToDictionary(s => s.SlotId);
        var stats = elements.Any(e => e.Type == LayoutElementType.SlotStats) ? ComputeStats(floor) : null;

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
            Stats = e.Type == LayoutElementType.SlotStats ? stats : null,
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
                    slot.LastExitTime
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
