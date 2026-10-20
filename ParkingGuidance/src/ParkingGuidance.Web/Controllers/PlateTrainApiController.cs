using Microsoft.AspNetCore.Mvc;
using OpenCvSharp;
using ParkingGuidance.Web.Services.Anpr;
using ParkingGuidance.Web.Services.Camera;
using ParkingGuidance.Web.Services.PlateTrain;

namespace ParkingGuidance.Web.Controllers;

/// <summary>Hệ thống › Camera › Train › "Train biển số": gán khung biển số trên ảnh, huấn luyện thêm bộ dò biển số (YOLO11, 1 lớp), cài / khôi phục model.</summary>
[ApiController]
[Route("api/system/plate-train")]
public class PlateTrainApiController : ControllerBase
{
    private const float SuggestConfidence = 0.30f;

    private readonly PlateTrainStore _store;
    private readonly PlateTrainer _trainer;
    private readonly IPlateDetectorModel _detector;
    private readonly AiJobScheduler _scheduler;
    private readonly ILogger<PlateTrainApiController> _logger;

    public PlateTrainApiController(PlateTrainStore store, PlateTrainer trainer, IPlateDetectorModel detector,
        AiJobScheduler scheduler, ILogger<PlateTrainApiController> logger)
    {
        _store = store;
        _trainer = trainer;
        _detector = detector;
        _scheduler = scheduler;
        _logger = logger;
    }

    public class BoxDto
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }
    public class SaveBoxesDto
    {
        public List<BoxDto> Boxes { get; set; } = new();
        public bool Reviewed { get; set; }
    }
    public class StartDto
    {
        public int? Epochs { get; set; }
        public bool FromScratch { get; set; }
    }

    private static object Summary(PlateSample s) => new { id = s.Id, name = s.ImageName, width = s.Width, height = s.Height, boxes = s.Boxes.Count, reviewed = s.Reviewed };

    // ------------------------------------------------------------------ thong tin chung
    [HttpGet("info")]
    public IActionResult Info()
    {
        var all = _store.List();
        return Ok(new
        {
            minSamples = PlateTrainer.MinSamples,
            counts = new
            {
                total = all.Count,
                reviewed = all.Count(s => s.Reviewed),
                boxes = all.Where(s => s.Reviewed).Sum(s => s.Boxes.Count),
                empty = all.Count(s => s.Reviewed && s.Boxes.Count == 0)
            },
            detector = new { ready = _detector.IsReady, info = _detector.ModelInfo, custom = _detector.UsingCustomModel, error = _detector.LoadError }
        });
    }

    // ------------------------------------------------------------------ anh
    [HttpGet("samples")]
    public IActionResult List() => Ok(_store.List().OrderBy(s => s.Reviewed).ThenByDescending(s => s.Added).Select(Summary));

    [HttpGet("samples/{id}")]
    public IActionResult Get(string id)
    {
        var s = _store.Get(id);
        return s is null ? NotFound(new { message = "Không tìm thấy ảnh." }) : Ok(s);
    }

    [HttpGet("samples/{id}/image")]
    public IActionResult Image(string id)
    {
        var p = _store.ImagePath(id);
        if (p is null || !System.IO.File.Exists(p)) return NotFound();
        var ext = Path.GetExtension(p).ToLowerInvariant();
        var type = ext switch { ".png" => "image/png", ".bmp" => "image/bmp", ".webp" => "image/webp", _ => "image/jpeg" };
        return PhysicalFile(p, type);
    }

    [HttpGet("samples/{id}/thumb")]
    public IActionResult Thumb(string id)
    {
        var p = _store.ThumbPath(id);
        return p is null ? NotFound() : PhysicalFile(p, "image/jpeg");
    }

    /// <summary>Thêm nhiều ảnh. suggest=true: tự gán khung bằng model đang chạy (chưa duyệt, người dùng kiểm tra lại).</summary>
    [HttpPost("samples")]
    [RequestSizeLimit(600_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 600_000_000)]
    public async Task<IActionResult> Upload([FromForm] IFormFileCollection files, [FromQuery] bool suggest = true)
    {
        if (files is null || files.Count == 0) return BadRequest(new { message = "Chưa chọn ảnh nào." });

        var added = new List<object>();
        var failed = new List<object>();
        int existing = 0;
        foreach (var f in files)
        {
            byte[] bytes;
            using (var ms = new MemoryStream()) { await f.CopyToAsync(ms, HttpContext.RequestAborted); bytes = ms.ToArray(); }

            var (sample, already, error) = _store.Add(f.FileName, bytes);
            if (sample is null) { failed.Add(new { name = f.FileName, error }); continue; }
            if (already) { existing++; continue; }

            if (suggest && _detector.IsReady)
            {
                try
                {
                    var boxes = await SuggestAsync(bytes);
                    var (saved, _) = _store.SaveBoxes(sample.Id, boxes, reviewed: false);
                    if (saved is not null) sample = saved;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger.LogWarning(ex, "Train bien so: khong goi y duoc khung cho {Name}.", f.FileName); }
            }
            added.Add(Summary(sample));
        }
        return Ok(new { added, existing, failed });
    }

    /// <summary>Gợi ý khung biển cho 1 ảnh bằng bộ dò đang chạy (khung ôm sát biển). Không lưu — trình duyệt hiển thị để người dùng sửa rồi mới lưu.</summary>
    [HttpPost("samples/{id}/suggest")]
    public async Task<IActionResult> Suggest(string id)
    {
        var p = _store.ImagePath(id);
        if (p is null || !System.IO.File.Exists(p)) return NotFound(new { message = "Không tìm thấy ảnh." });
        if (!_detector.IsReady) return Conflict(new { message = "Chưa có bộ dò biển số nào được nạp để gợi ý khung." });
        var boxes = await SuggestAsync(await System.IO.File.ReadAllBytesAsync(p, HttpContext.RequestAborted));
        return Ok(new { boxes });
    }

    private async Task<List<PlateBox>> SuggestAsync(byte[] imageBytes)
    {
        // chay qua hang doi AI chung (uu tien cao: nguoi dung dang cho) de khong tranh tai nguyen voi camera that
        return await _scheduler.RunInteractiveAsync("Gợi ý khung biển số (Train biển số)", ct =>
        {
            using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            var list = new List<PlateBox>();
            if (mat.Empty()) return Task.FromResult(list);
            foreach (var d in _detector.SuggestBoxes(mat, SuggestConfidence))
                list.Add(new PlateBox { X1 = d.X1, Y1 = d.Y1, X2 = d.X2, Y2 = d.Y2 });
            return Task.FromResult(list);
        }, HttpContext.RequestAborted);
    }

    [HttpPut("samples/{id}")]
    public IActionResult Save(string id, [FromBody] SaveBoxesDto dto)
    {
        var boxes = (dto.Boxes ?? new()).Select(b => new PlateBox { X1 = b.X1, Y1 = b.Y1, X2 = b.X2, Y2 = b.Y2 });
        var (s, error) = _store.SaveBoxes(id, boxes, dto.Reviewed);
        return error is not null ? BadRequest(new { message = error }) : Ok(s);
    }

    [HttpDelete("samples/{id}")]
    public IActionResult Delete(string id) =>
        _store.Delete(id) ? Ok(new { deleted = true }) : NotFound(new { message = "Không tìm thấy ảnh." });

    [HttpDelete("samples")]
    public IActionResult Clear() => Ok(new { deleted = _store.Clear() });

    // ------------------------------------------------------------------ huan luyen
    [HttpGet("train/status")]
    public IActionResult TrainStatus() => Ok(_trainer.GetStatus());

    [HttpPost("train/start")]
    public IActionResult TrainStart([FromBody] StartDto? dto)
    {
        var (ok, message) = _trainer.Start(dto?.Epochs, dto?.FromScratch ?? false);
        return ok ? Ok(new { message }) : Conflict(new { message });
    }

    [HttpPost("train/cancel")]
    public IActionResult TrainCancel()
    {
        var (ok, message) = _trainer.Cancel();
        return ok ? Ok(new { message }) : Conflict(new { message });
    }

    /// <summary>"Vẫn cài bản mới": cài model vừa train dù chỉ số không vượt model đang chạy.</summary>
    [HttpPost("train/install")]
    public IActionResult TrainInstall()
    {
        if (_trainer.Running) return Conflict(new { message = "Đang huấn luyện." });
        var r = _trainer.Install(force: true);
        return r.Ok ? Ok(r) : Conflict(r);
    }

    /// <summary>Bỏ model đã train thêm, quay về bộ dò biển mặc định.</summary>
    [HttpPost("train/restore")]
    public IActionResult TrainRestore()
    {
        var (ok, message) = _trainer.RestoreBase();
        return ok ? Ok(new { message }) : Conflict(new { message });
    }

    [HttpPost("train/clear")]
    public IActionResult TrainClear()
    {
        var (ok, message) = _trainer.ClearResult();
        return ok ? Ok(new { message }) : Conflict(new { message });
    }
}
