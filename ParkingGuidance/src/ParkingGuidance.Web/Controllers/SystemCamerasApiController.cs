using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using OpenCvSharp;
using ParkingGuidance.Web.Hubs;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Anpr;
using ParkingGuidance.Web.Services.Camera;
using ParkingGuidance.Web.Services.CameraRegistry;
using ParkingGuidance.Web.Services.Detection;
using ParkingGuidance.Web.Services.Parking;
using ParkingGuidance.Web.Services.Settings;

namespace ParkingGuidance.Web.Controllers;

[ApiController]
[Route("api/system/cameras")]
public class SystemCamerasApiController : ControllerBase
{
    private readonly ICameraRegistryService _registry;
    private readonly IOnvifDiscoveryService _discovery;
    private readonly IVehicleDetectionService _detector;
    private readonly IPlateRecognitionService _anpr;
    private readonly IParkingService _parking;
    private readonly ICameraFrameProcessor _frameProcessor;
    private readonly IFrameChangeDetector _changeDetector;
    private readonly ISettingsService _settings;
    private readonly AiJobScheduler _scheduler;
    private readonly IHubContext<MonitoringHub> _hub;

    public SystemCamerasApiController(
        ICameraRegistryService registry,
        IOnvifDiscoveryService discovery,
        IVehicleDetectionService detector,
        IPlateRecognitionService anpr,
        IParkingService parking,
        ICameraFrameProcessor frameProcessor,
        IFrameChangeDetector changeDetector,
        ISettingsService settings,
        AiJobScheduler scheduler,
        IHubContext<MonitoringHub> hub)
    {
        _hub = hub;
        _registry = registry;
        _discovery = discovery;
        _detector = detector;
        _anpr = anpr;
        _parking = parking;
        _frameProcessor = frameProcessor;
        _changeDetector = changeDetector;
        _settings = settings;
        _scheduler = scheduler;
    }

    private TimeSpan OpenTimeout => TimeSpan.FromSeconds(_settings.Current.OpenTimeoutSeconds);
    private TimeSpan ReadTimeout => TimeSpan.FromSeconds(_settings.Current.FrameReadTimeoutSeconds);

    /// <summary>Danh mục các hãng camera thông dụng, dùng cho combobox lọc ở tab "Danh sách".</summary>
    [HttpGet("brands")]
    public IActionResult GetBrands() => Ok(CameraBrandCatalog.All);

    /// <summary>Danh sách camera đã khai báo trong hệ thống (đã thêm bằng nút "Thêm camera" hoặc
    /// từ kết quả dò mạng), bao gồm cả camera chưa "Thêm vào hệ thống bãi đỗ".</summary>
    [HttpGet]
    public IActionResult GetAll() => Ok(_registry.GetAll());

    /// <summary>
    /// Dò tìm camera trên mạng LAN theo hãng đã chọn (KHÔNG tự động — chỉ chạy khi người dùng bấm
    /// "Quét lại"). Có thể mất vài giây vì phải chờ camera phản hồi.
    /// </summary>
    [HttpGet("discover")]
    public async Task<IActionResult> Discover(
        [FromQuery] string? brand, [FromQuery] int timeoutMs, CancellationToken ct)
    {
        var keyword = CameraBrandCatalog.All.FirstOrDefault(b =>
            string.Equals(b.Name, brand, StringComparison.OrdinalIgnoreCase))?.OnvifKeyword;

        var timeout = TimeSpan.FromMilliseconds(timeoutMs is > 500 and <= 15000 ? timeoutMs : 3000);
        var result = await _discovery.DiscoverAsync(string.IsNullOrWhiteSpace(keyword) ? null : keyword, timeout, ct);

        foreach (var cam in result.Cameras)
            cam.AlreadyAdded = _registry.IsIpRegistered(cam.IpAddress, cam.Port);

        return Ok(new { cameras = result.Cameras, diagnostics = result.Diagnostics });
    }

    public class AddCameraDto
    {
        public string IpAddress { get; set; } = "";
        public int Port { get; set; } = 80;
        public string Brand { get; set; } = "";
        public string? Model { get; set; }
        public string? OnvifServiceUrl { get; set; }
    }

    /// <summary>Thêm 1 camera vào danh sách theo IP — dùng cho cả "+ Thêm camera" (nhập tay) lẫn
    /// nút "Thêm" trên từng dòng kết quả dò mạng. Kiểm tra trùng IP:Port trước khi thêm.</summary>
    [HttpPost]
    public IActionResult Add([FromBody] AddCameraDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.IpAddress))
            return BadRequest(new { message = "Thieu dia chi IP." });

        if (dto.Port is <= 0 or > 65535)
            return BadRequest(new { message = "Cong khong hop le (1-65535)." });

        try
        {
            var camera = _registry.Add(dto.IpAddress.Trim(), dto.Port, dto.Brand ?? "", dto.Model, dto.OnvifServiceUrl);
            return Ok(camera);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    public class UpdateCameraInfoDto
    {
        public string? Floor { get; set; }

        /// <summary>Zone (khu vực lắp đặt) trong tầng hầm: số tự nhiên nguyên dương. Phải đi cùng <see cref="Floor"/>.</summary>
        public string? Zone { get; set; }
        public string? Notes { get; set; }
        public string? VideoSource { get; set; }
    }

    /// <summary>Khoá các ô đỗ của các vùng <paramref name="zones"/> khi camera nằm ở tầng <paramref name="floor"/>, Zone <paramref name="zone"/>.</summary>
    private static List<string> SlotKeysIn(string? floor, string? zone, IEnumerable<DetectionZone> zones) =>
        zones.Select(z => SlotKey.ForZone(floor, zone, z.SlotId)).Where(k => k is not null).Select(k => k!).Distinct().ToList();

    /// <summary>Các số thứ tự vị trí nhận diện ĐÃ DÙNG trong (tầng, Zone) bởi các camera khác <paramref name="exceptCameraId"/>.</summary>
    private HashSet<string> TakenNumbersIn(string floor, string zone, string? exceptCameraId) =>
        _registry.GetAll()
            .Where(c => c.Id != exceptCameraId
                        && SlotKey.NormalizeFloor(c.Floor) == floor
                        && SlotKey.NormalizeZone(c.Zone) == zone)
            .SelectMany(c => c.Zones)
            .Select(z => SlotKey.TryNormalizeNumber(z.SlotId, out var n) ? n : null)
            .Where(n => n is not null).Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

    [HttpPut("{id}")]
    public IActionResult UpdateInfo(string id, [FromBody] UpdateCameraInfoDto dto)
    {
        var existing = _registry.Get(id);
        if (existing is null) return NotFound(new { message = "Khong tim thay camera." });

        // Tang hầm + Zone di cung nhau: co tang thi phai co Zone (so tu nhien nguyen duong); khong co tang thi khong co Zone.
        var hasFloor = !string.IsNullOrWhiteSpace(dto.Floor);
        string? newZoneValue = null;
        if (!string.IsNullOrWhiteSpace(dto.Zone))
        {
            if (!hasFloor)
                return BadRequest(new { message = "Can chon tang ham truoc khi gan Zone." });
            if (!SlotKey.TryNormalizeNumber(dto.Zone, out var zn))
                return BadRequest(new { message = $"{SlotKey.ZoneInvalidMessage} Gia tri khong hop le: '{dto.Zone}'." });
            newZoneValue = zn;
        }
        else if (hasFloor)
        {
            return BadRequest(new { message = "Can nhap Zone (khu vuc lap dat trong tang ham) cho camera. Zone la so tu nhien nguyen duong (1, 2, 3...)." });
        }

        // Doi tang hoac Zone cua camera da ve vung: so thu tu cua cac vung khong duoc trung voi vung san co o Zone moi,
        // cac o cu (vi tri cu) duoc dua ve "Con trong" va o moi duoc tao o vi tri moi.
        var oldFloor = SlotKey.NormalizeFloor(existing.Floor);
        var oldZone = SlotKey.NormalizeZone(existing.Zone);
        var newFloor = SlotKey.NormalizeFloor(dto.Floor);
        var newZone = SlotKey.NormalizeZone(newZoneValue);
        bool placeChanged = hasFloor && (oldFloor != newFloor || oldZone != newZone) && existing.Zones.Count > 0;

        if (placeChanged)
        {
            var taken = TakenNumbersIn(newFloor, newZone, id);
            var clash = existing.Zones
                .Select(z => SlotKey.TryNormalizeNumber(z.SlotId, out var n) ? n : null)
                .FirstOrDefault(n => n is not null && taken.Contains(n));
            if (clash is not null)
                return Conflict(new { message =
                    $"Khong doi sang tang {newFloor}, Zone {newZone} duoc: so thu tu vi tri '{clash}' da duoc dung o Zone nay. " +
                    "Hay doi so thu tu vi tri (tab Vi tri do) truoc." });

            foreach (var key in SlotKeysIn(oldFloor, oldZone, existing.Zones)) _parking.ResetSlot(key);
            foreach (var key in SlotKeysIn(newFloor, newZone, existing.Zones)) _parking.EnsureSlot(key, newFloor, newZone);
        }

        var camera = _registry.UpdateInfo(id, dto.Floor, newZoneValue, dto.Notes, dto.VideoSource);
        return camera is null ? NotFound(new { message = "Khong tim thay camera." }) : Ok(camera);
    }

    /// <summary>Chính thức đưa camera vào vận hành — từ lúc này camera xuất hiện trong các thao tác
    /// về sau (giám sát, dịch vụ đọc camera nền...). Yêu cầu đã gán tầng hầm và Zone.</summary>
    [HttpPost("{id}/commit")]
    public IActionResult Commit(string id)
    {
        var existing = _registry.GetAll().FirstOrDefault(c => c.Id == id);
        if (existing is null) return NotFound(new { message = "Khong tim thay camera." });

        if (string.IsNullOrWhiteSpace(existing.Floor))
            return BadRequest(new { message = "Can chon tang ham cho camera truoc khi them vao he thong." });

        if (string.IsNullOrWhiteSpace(existing.Zone))
            return BadRequest(new { message = "Can nhap Zone (khu vuc lap dat trong tang ham) cho camera truoc khi them vao he thong." });

        return Ok(_registry.Commit(id));
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var camera = _registry.Get(id);
        if (camera is null) return NotFound(new { message = "Khong tim thay camera." });

        // Cac o do camera nay quan ly: dua ve "Con trong" (khong con ai cap nhat nua)
        var usedElsewhere = _registry.GetAll().Where(c => c.Id != id)
            .SelectMany(c => SlotKeysIn(c.Floor, c.Zone, c.Zones)).ToHashSet(StringComparer.Ordinal);
        foreach (var key in SlotKeysIn(camera.Floor, camera.Zone, camera.Zones))
            if (!usedElsewhere.Contains(key))
                _parking.ResetSlot(key);

        _registry.Delete(id);
        return NoContent();
    }

    // -----------------------------------------------------------------------
    // Tab "Vi tri do": vung nhan dien (moi vung ung voi 1 o do) + nguong chuyen dong
    // -----------------------------------------------------------------------

    public class ZonesDto
    {
        public List<DetectionZone> Zones { get; set; } = new();
    }

    [HttpPut("{id}/zones")]
    public IActionResult UpdateZones(string id, [FromBody] ZonesDto dto)
    {
        var camera = _registry.Get(id);
        if (camera is null) return NotFound(new { message = "Khong tim thay camera." });

        if (string.IsNullOrWhiteSpace(camera.Zone))
            return BadRequest(new { message = "Camera chua gan Zone. Hay chon tang ham va nhap Zone o tab Danh sach truoc khi ve vi tri nhan dien." });

        var floor = SlotKey.NormalizeFloor(camera.Floor);
        var cameraZone = SlotKey.NormalizeZone(camera.Zone);

        // 0) So thu tu phai la SO TU NHIEN NGUYEN DUONG (1, 2, 3...). Dang "B1", "A-2", "1.5", "0", "-3" deu bi tu choi.
        foreach (var z in dto.Zones)
        {
            if (!SlotKey.TryNormalizeNumber(z.SlotId, out var number))
                return BadRequest(new { message = $"{SlotKey.InvalidMessage} Gia tri khong hop le: '{z.SlotId}'." });
            if (z.Points.Count < 3)
                return BadRequest(new { message = $"Vi tri so {number} can it nhat 3 diem." });

            z.SlotId = number;          // dang chuan (bo so 0 dung dau)
            z.Name = $"Ô {number}";
        }

        // 1) Trong cung 1 camera: khong duoc trung so thu tu
        var dup = dto.Zones.GroupBy(z => z.SlotId!).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            return Conflict(new { message = $"So thu tu {dup.Key} bi trung trong cung 1 Zone (tang {floor}, Zone {cameraZone}). Moi vi tri phai co so thu tu rieng." });

        // 2) Cac camera khac CUNG TANG va CUNG ZONE: khong duoc trung so thu tu (Zone khac / tang khac thi duoc dung lai cung so)
        foreach (var other in _registry.GetAll().Where(c => c.Id != id
                     && SlotKey.NormalizeFloor(c.Floor) == floor && SlotKey.NormalizeZone(c.Zone) == cameraZone))
        {
            var clash = other.Zones.FirstOrDefault(oz =>
                SlotKey.TryNormalizeNumber(oz.SlotId, out var otherNumber) && dto.Zones.Any(z => z.SlotId == otherNumber));
            if (clash is not null)
                return Conflict(new { message =
                    $"So thu tu {clash.SlotId} da duoc dung o tang {floor}, Zone {cameraZone} (camera {other.IpAddress}). " +
                    "Trong 1 Zone khong the co so thu tu vi tri giong nhau." });
        }

        // Dam bao o do da khai bao trong ParkingService truoc khi camera co the "bao cao" xe vao o nay
        foreach (var z in dto.Zones)
            _parking.EnsureSlot(SlotKey.Make(floor, cameraZone, z.SlotId!), floor, cameraZone);

        // O mat vung (xoa vung / doi so thu tu) va khong con vung nao khac dung: dua ve "Con trong", dong phien dang mo
        var newKeys = SlotKeysIn(floor, cameraZone, dto.Zones).ToHashSet(StringComparer.Ordinal);
        var usedElsewhere = _registry.GetAll().Where(c => c.Id != id)
            .SelectMany(c => SlotKeysIn(c.Floor, c.Zone, c.Zones)).ToHashSet(StringComparer.Ordinal);
        foreach (var old in SlotKeysIn(floor, cameraZone, camera.Zones))
            if (!newKeys.Contains(old) && !usedElsewhere.Contains(old))
                _parking.ResetSlot(old);

        var updated = _registry.UpdateZones(id, dto.Zones);
        return updated is null ? NotFound(new { message = "Khong tim thay camera." }) : Ok(updated);
    }

    public class MotionThresholdDto
    {
        public double? MotionThreshold { get; set; }
    }

    [HttpPut("{id}/motion-threshold")]
    public IActionResult UpdateMotionThreshold(string id, [FromBody] MotionThresholdDto dto)
    {
        var camera = _registry.UpdateMotionThreshold(id, dto.MotionThreshold);
        return camera is null ? NotFound(new { message = "Khong tim thay camera." }) : Ok(camera);
    }

    public class DetectionModeDto
    {
        public string Mode { get; set; } = "Automatic"; // "Automatic" | "Manual"
    }

    /// <summary>"Automatic": camera chạy trong vòng lặp nền (CameraIngestionService).
    /// "Manual": KHÔNG tự chạy nền — chỉ nhận diện khi gọi POST {id}/detect-now.</summary>
    [HttpPut("{id}/detection-mode")]
    public IActionResult UpdateDetectionMode(string id, [FromBody] DetectionModeDto dto)
    {
        var camera = _registry.UpdateDetectionMode(id, dto.Mode);
        return camera is null ? NotFound(new { message = "Khong tim thay camera." }) : Ok(camera);
    }

    public class UiSettingsDto
    {
        public string SourceMode { get; set; } = "Pc";            // "Camera" | "Pc"
        public int CaptureIntervalSeconds { get; set; } = 10;
        public bool AutoCapture { get; set; }
        public bool AutoDetect { get; set; } = true;
        public bool RegisterToParking { get; set; } = true;
    }

    /// <summary>Lưu thiết lập giao diện của tab Vị trí đỗ cho camera (nguồn ảnh, chu kỳ tự lấy ảnh, tự nhận diện)
    /// để mở lại vẫn đúng như lần trước.</summary>
    [HttpPut("{id}/ui-settings")]
    public IActionResult UpdateUiSettings(string id, [FromBody] UiSettingsDto dto)
    {
        var camera = _registry.UpdateUiSettings(id, dto.SourceMode, dto.CaptureIntervalSeconds, dto.AutoCapture, dto.AutoDetect, dto.RegisterToParking);
        return camera is null ? NotFound(new { message = "Khong tim thay camera." }) : Ok(camera);
    }

    /// <summary>
    /// Lấy ĐÚNG 1 khung hình hiện tại từ "Nguồn video" của camera (không xem liên tục) — dùng cho
    /// chế độ "📷 Lấy từ Camera" ở tab Vị trí đỗ. Trả về ảnh JPEG trực tiếp, có thể gán thẳng vào
    /// thuộc tính src của thẻ &lt;img&gt;. Gọi lại nhiều lần = chụp lại nhiều lần (phía client tự
    /// quyết định chu kỳ gọi, server không tự lặp).
    /// </summary>
    [HttpGet("{id}/snapshot")]
    public async Task<IActionResult> GetSnapshot(string id)
    {
        var camera = _registry.GetAll().FirstOrDefault(c => c.Id == id);
        if (camera is null) return NotFound(new { message = "Khong tim thay camera." });

        if (string.IsNullOrWhiteSpace(camera.VideoSource))
            return BadRequest(new { message = "Camera chua khai bao 'Nguon video' o tab Danh sach." });

        // Co timeout: camera mat ket noi chi lam yeu cau nay that bai, khong treo luong server.
        var (frame, error) = await VideoCaptureHelper.GrabOnceAsync(camera.VideoSource, OpenTimeout, ReadTimeout);
        if (frame is null) return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = error });

        using (frame)
            return File(frame.ToBytes(".jpg"), "image/jpeg");
    }

    /// <summary>
    /// Nhận diện THỦ CÔNG: mở "Nguồn video" của camera, đọc đúng 1 khung hình hiện tại, chạy AI
    /// (áp dụng vùng nhận diện nếu có), GHI NHẬN kết quả vào ParkingService (khác với "test-detect"
    /// chỉ để xem trước trên ảnh mô phỏng, không ghi nhận). Dùng cho camera ở chế độ "Thủ công".
    /// </summary>
    [HttpPost("{id}/detect-now")]
    public async Task<IActionResult> DetectNow(string id, CancellationToken ct)
    {
        var camera = _registry.GetAll().FirstOrDefault(c => c.Id == id);
        if (camera is null) return NotFound(new { message = "Khong tim thay camera." });

        if (string.IsNullOrWhiteSpace(camera.VideoSource))
            return BadRequest(new { message = "Camera chua khai bao 'Nguon video' (RTSP/file) o tab Danh sach." });

        var (grabbed, error) = await VideoCaptureHelper.GrabOnceAsync(camera.VideoSource, OpenTimeout, ReadTimeout);
        if (grabbed is null) return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = error });
        using var frame = grabbed;

        // Xep vao hang doi AI voi uu tien cao (khong chay AI truc tiep tren luong web de khong tranh tai voi cac camera nen)
        var zones = camera.Zones.ToList();
        var registeredCount = await _scheduler.RunInteractiveAsync(
            $"Nhan dien ngay ({camera.IpAddress})",
            qct =>
            {
                // Nut "Nhan dien ngay" = nhan dien LAI TU DAU, KHONG kiem tra sai khac so voi anh truoc:
                // xoa du lieu nhan dien cu cua cac o thuoc camera nay (dong phien do, o ve "Con trong", bo bien cu)
                // roi chay lai AI tren khung vua lay.
                foreach (var key in SlotKeysIn(camera.Floor, camera.Zone, zones))
                {
                    _parking.EnsureSlot(key, camera.Floor ?? "B1", camera.Zone);
                    _parking.ResetSlot(key);
                }
                return _frameProcessor.ProcessAsync(camera.Id, frame, zones, null, camera.Floor, camera.Zone, qct, manual: true);
            }, ct);

        // Nhan dien xong (khong loi) -> luu anh nay lam MOC so sanh ty le sai khac cho cac lan sau
        // (key = CameraId cho vong lap nen khi chuyen sang Tu dong; "ui:{id}" cho tab Vi tri do).
        _changeDetector.SetReference(camera.Id, frame);
        _changeDetector.SetReference($"ui:{camera.Id}", frame);

        await _hub.Clients.All.SendAsync("SlotsChanged", ct);

        return Ok(new
        {
            message = registeredCount > 0
                ? $"Đã nhận diện và ghi nhận {registeredCount} xe."
                : "Đã chạy nhận diện nhưng không có xe nào khớp vùng đã định nghĩa.",
            registeredCount
        });
    }

    /// <summary>
    /// Chạy thử nhận diện AI trên 1 ảnh (thật hoặc ảnh mô phỏng chọn từ máy tính) ÁP DỤNG các vùng
    /// nhận diện đã vẽ cho camera: với mỗi xe phát hiện được, tìm vùng có tỉ lệ diện tích xe che
    /// phủ cao nhất; chỉ tính là "đang đỗ ở ô đó" khi tỉ lệ ≥ 50% (phần lớn xe nằm trong vùng).
    /// Xe không thuộc vùng nào (hoặc camera chưa vẽ vùng nào) vẫn được trả về nhưng đánh dấu
    /// zoneId/slotId = null để người dùng thấy được là "phát hiện được nhưng ngoài mọi ô đỗ".
    /// </summary>
    [HttpPost("{id}/test-detect")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> TestDetect(
        string id, IFormFile image, [FromQuery] bool onlyIfChanged = false, [FromQuery] bool register = false)
    {
        var camera = _registry.GetAll().FirstOrDefault(c => c.Id == id);
        if (camera is null) return NotFound(new { message = "Khong tim thay camera." });

        if (image is null || image.Length == 0)
            return BadRequest(new { message = "Thieu anh dau vao." });

        await using var stream = image.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        using var mat = Mat.FromImageData(ms.ToArray(), ImreadModes.Color);
        if (mat.Empty())
            return BadRequest(new { message = "Khong doc duoc anh." });

        // Che do tu dong (anh moi tu camera HOAC tu PC): chi chay AI khi anh thay doi vuot nguong cua camera.
        // Dung key rieng "ui:{id}" de khung tham chieu cua giao dien KHONG lam lech khung tham chieu cua vong lap nen.
        if (onlyIfChanged)
        {
            var threshold = camera.MotionThreshold ?? _settings.Current.DefaultMotionThresholdPercent;
            var change = _changeDetector.Evaluate($"ui:{camera.Id}", mat, threshold);
            if (!change.ShouldProcess)
                return Ok(new { skipped = true, changePercent = Math.Round(change.ChangePercent, 2), threshold });
        }

        var zones = camera.Zones.ToList();
        var regItems = new List<(string Key, DetectionResult Det)>(); // xe da gan vung (de ghi nhan vao bai do neu bat 'register')
        var (vehicles, results) = await _scheduler.RunInteractiveAsync($"Chay thu nhan dien ({camera.IpAddress})", qct =>
        {
        // Cung luong voi nen: do xe va do khung bien SONG SONG, bien nam ngoai moi xe bi bo
        var st = _settings.Current;
        var (detected, plateBoxes) = FrameAnalysis.DetectParallel(_detector, _anpr, mat, st, detectPlates: true);
        var plateAssign = FrameAnalysis.AssignPlates(detected.Select(d => d.Box).ToList(), plateBoxes, st.PlateMinOverlap ?? 0.1f);
        var rows = new List<TestDetectVehicleResult>();

        // Cung quy tac voi nen: moi vung toi da 1 xe (xe chiem nhieu dien tich trong vung nhat thang)
        var assignments = ZoneUtils.AssignVehiclesToZones(
            detected.Select(d => d.Box).ToList(), zones, mat.Width, mat.Height);
        var assignedByVehicle = assignments.ToDictionary(a => a.VehicleIndex);

        for (int i = 0; i < detected.Count; i++)
        {
            var v = detected[i];
            assignedByVehicle.TryGetValue(i, out var assigned);
            var hasMatch = assignedByVehicle.ContainsKey(i);

            // Xe khong duoc gan: phan biet "ngoai vung" voi "vung da co xe khac" de nguoi dung hieu vi sao bi loai
            string? reason = null;
            double reasonCoverage = 0;
            if (!hasMatch)
            {
                var near = ZoneUtils.FindBestZone(v.Box, zones, mat.Width, mat.Height);
                reason = near is null ? "outside" : "zoneTaken";
                reasonCoverage = near?.Coverage ?? 0;
            }

            string? plate = null;
            float plateConf = 0;
            if (hasMatch) // chi doc bien so cho xe DA DUOC GAN vao 1 vung (tiet kiem chi phi OCR)
            {
                if (plateAssign.TryGetValue(i, out var plateBox))
                {
                    var (p, c, _) = _anpr.ReadPlateAt(mat, plateBox);
                    plate = p; plateConf = c;
                }
                else if ((st.PlateFullSearch ?? 0) != 0)   // tuy chon (mac dinh tat): tim bien day du tren anh xe — cham
                {
                    var rect = BoxUtils.ClampToImage(mat, v.Box, BoxUtils.PlateSearchExpand);
                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        using var crop = new Mat(mat, rect);
                        var (p, c, _) = _anpr.Recognize(crop);
                        plate = p; plateConf = c;
                    }
                }

                var key = SlotKey.ForZone(camera.Floor, camera.Zone, assigned.Zone.SlotId);
                if (key is not null)
                {
                    v.PlateNumber = plate;
                    v.PlateConfidence = plateConf;
                    regItems.Add((key, v));
                }
            }

            rows.Add(new TestDetectVehicleResult
            {
                Box = v.Box,
                VehicleType = v.VehicleType.ToString(),
                Confidence = v.Confidence,
                Color = v.Color,
                ZoneId = hasMatch ? assigned.Zone.Id : null,
                SlotId = hasMatch ? assigned.Zone.SlotId : null,
                Coverage = hasMatch ? assigned.Coverage : reasonCoverage,
                Reason = reason,
                PlateNumber = plate,
                PlateConfidence = plateConf
            });
        }
        rows = rows.OrderBy(r => r.Box.X1).ToList();

        return Task.FromResult((detected, rows));
        }, HttpContext.RequestAborted);

        if (onlyIfChanged) _changeDetector.CommitReference($"ui:{camera.Id}"); // nhan dien xong -> chot anh tham chieu moi

        // GHI NHAN vao bai do (cap nhat ban do / giam sat) — dung cho anh tu PC hoac anh lay tay tu camera: xe thay trong vung -> o
        // "Da do" + bien so; vung khong con xe -> xac nhan xe da roi NGAY (nguoi dung chu dong dua anh vao nen khong can cho nhieu lan).
        int registered = 0, vacated = 0;
        if (register)
        {
            foreach (var (key, det) in regItems)
            {
                _parking.EnsureSlot(key, camera.Floor ?? "B1", camera.Zone);
                var evt = _parking.RegisterDetection(camera.Id, key, det, immediate: true);
                registered++;
                await _hub.Clients.All.SendAsync("ParkingEvent", new
                {
                    evt.CameraId, evt.SlotId, evt.PlateNumber, evt.EventType, Timestamp = evt.Timestamp,
                    VehicleType = det.VehicleType.ToString(), det.Color, Confidence = det.Confidence
                });
            }

            var occupied = regItems.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in SlotKeysIn(camera.Floor, camera.Zone, zones).Where(k => !occupied.Contains(k)))
            {
                var exit = _parking.RegisterVacancy(camera.Id, key, 1);
                if (exit is null) continue;
                vacated++;
                await _hub.Clients.All.SendAsync("ParkingEvent", new
                {
                    exit.CameraId, exit.SlotId, exit.PlateNumber, exit.EventType, Timestamp = exit.Timestamp
                });
            }
            await _hub.Clients.All.SendAsync("SlotsChanged");
        }

        return Ok(new
        {
            FrameWidth = mat.Width,
            FrameHeight = mat.Height,
            VehicleCount = vehicles.Count,
            MatchedCount = results.Count(r => r.SlotId != null),
            Registered = registered,
            Vacated = vacated,
            Vehicles = results
        });
    }

    public class TestDetectVehicleResult
    {
        public BoundingBox Box { get; set; } = new();
        public string VehicleType { get; set; } = "";
        public float Confidence { get; set; }
        public string? Color { get; set; }
        public string? ZoneId { get; set; }
        public string? SlotId { get; set; }
        public double Coverage { get; set; }

        /// <summary>Lý do xe KHÔNG được gán vùng: "outside" = ngoài mọi vùng; "zoneTaken" = vùng đã có xe khác lớn hơn. Null = đã gán.</summary>
        public string? Reason { get; set; }
        public string? PlateNumber { get; set; }
        public float PlateConfidence { get; set; }
    }
}
