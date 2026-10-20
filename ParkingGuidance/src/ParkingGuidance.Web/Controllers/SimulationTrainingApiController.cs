using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Services.Simulation;

namespace ParkingGuidance.Web.Controllers;

/// <summary>
/// Tab "Train": hàng chờ ảnh (thêm từ tab Mô phỏng hoặc nạp thẳng), các mẫu biển đã cắt + gán ký tự, và điều khiển việc dạy lại
/// model đọc ký tự từ chúng. Không giữ gì trong bộ nhớ: mọi thứ nằm trên đĩa; train chạy ở tiến trình riêng, ưu tiên thấp, chỉ khi bấm Train.
/// </summary>
[ApiController]
[Route("api/system/simulation")]
public class SimulationTrainingApiController : ControllerBase
{
    private readonly SimulationCorrectionStore _store;
    private readonly SimulationTrainer _trainer;
    private readonly TrainingInboxStore _inbox;

    public SimulationTrainingApiController(SimulationCorrectionStore store, SimulationTrainer trainer, TrainingInboxStore inbox)
    {
        _store = store;
        _trainer = trainer;
        _inbox = inbox;
    }

    // ------------------------------------------------------------------ hàng chờ ảnh (thêm từ tab Mô phỏng)
    [HttpGet("inbox")]
    public IActionResult ListInbox()
    {
        var corr = _store.List();
        var items = _inbox.List()
            .OrderBy(e => e.Done)                 // chưa xử lý lên trước
            .ThenByDescending(e => e.Added)
            .Select(e => new
            {
                e.Id, e.ImageName, e.Source, e.Done, e.Added, e.Hint,
                Samples = corr.Count(c => c.Key.StartsWith(e.Id + "|", StringComparison.Ordinal))
            })
            .ToList();
        return Ok(new { count = items.Count, pending = items.Count(i => !i.Done), items });
    }

    /// <summary>Thêm 1 ảnh gốc vào hàng chờ kèm gợi ý (khung biển + biển đã đọc, JSON). Ảnh trùng nội dung chỉ được giữ 1 bản.</summary>
    [HttpPost("inbox")]
    [RequestSizeLimit(40_000_000)]
    public async Task<IActionResult> AddToInbox(
        IFormFile image, [FromForm] string? name, [FromForm] string? hint, [FromForm] string? source, CancellationToken ct)
    {
        if (image is null || image.Length == 0) return BadRequest(new { message = "Thiếu ảnh." });
        try
        {
            using var ms = new MemoryStream();
            await image.CopyToAsync(ms, ct);
            var (e, isNew) = _inbox.Add(string.IsNullOrWhiteSpace(name) ? image.FileName : name, ms.ToArray(), hint, source);
            var all = _inbox.List();
            return Ok(new { e.Id, isNew, count = all.Count, pending = all.Count(x => !x.Done) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("inbox/{id}/image")]
    public IActionResult InboxImage(string id)
    {
        var path = _inbox.ImagePath(id);
        return path is null ? NotFound() : PhysicalFile(path, TrainingInboxStore.ContentTypeFor(path));
    }

    [HttpGet("inbox/{id}/thumb")]
    public IActionResult InboxThumb(string id)
    {
        var bytes = _inbox.Thumbnail(id);
        return bytes is null ? NotFound() : File(bytes, "image/jpeg");
    }

    /// <summary>Đánh dấu ảnh đã xử lý xong (<c>done=true</c>) hoặc chưa xong.</summary>
    [HttpPost("inbox/{id}/done")]
    public IActionResult InboxDone(string id, [FromQuery] bool done = true) =>
        _inbox.SetDone(id, done) ? Ok(new { id, done }) : NotFound(new { message = "Không thấy ảnh trong hàng chờ." });

    /// <summary>Bỏ ảnh khỏi hàng chờ. <paramref name="withSamples"/>=true: xoá luôn các mẫu biển đã cắt từ ảnh này.</summary>
    [HttpDelete("inbox/{id}")]
    public IActionResult DeleteInbox(string id, [FromQuery] bool withSamples = true)
    {
        if (!TrainingInboxStore.IsValidId(id)) return BadRequest(new { message = "Mã ảnh không hợp lệ." });
        var removedSamples = withSamples ? _store.DeleteByKeyPrefix(id + "|") : 0;
        var deleted = _inbox.Delete(id);
        return Ok(new { deleted, removedSamples, samples = _store.List().Count });
    }

    /// <summary>Gỡ các ảnh đã xử lý xong khỏi hàng chờ (mẫu đã lưu vẫn giữ nguyên).</summary>
    [HttpPost("inbox/clear-done")]
    public IActionResult ClearInboxDone() => Ok(new { deleted = _inbox.ClearDone() });

    // ------------------------------------------------------------------ mẫu đã sửa / xác nhận
    [HttpGet("corrections")]
    public IActionResult ListCorrections()
    {
        var items = _store.List()
            .OrderByDescending(e => e.Updated)
            .Select(e => new
            {
                e.Id, e.Key, e.ImageName, e.Kind, e.Correct, e.Recognized, e.Updated, e.Meta,
                Changed = e.Correct != e.Recognized   // true = hệ thống đã đọc sai/không đọc được → mẫu quý để học
            })
            .ToList();
        return Ok(new { count = items.Count, changed = items.Count(i => i.Changed), items });
    }

    /// <summary>Thêm/sửa 1 mẫu: ảnh biển (PNG, cắt từ ảnh gốc ở trình duyệt) + biển đúng + biển hệ thống đã đọc (+ <c>meta</c>: khung cắt, tuỳ chọn).</summary>
    [HttpPut("corrections")]
    [RequestSizeLimit(40_000_000)]
    public async Task<IActionResult> UpsertCorrection(
        [FromForm] string key, [FromForm] string? imageName, [FromForm] string? kind,
        [FromForm] string correct, [FromForm] string? recognized, [FromForm] string? meta, IFormFile image, CancellationToken ct)
    {
        if (image is null || image.Length == 0) return BadRequest(new { message = "Thiếu ảnh biển số." });
        try
        {
            using var ms = new MemoryStream();
            await image.CopyToAsync(ms, ct);
            var e = _store.Upsert(key, imageName ?? "", kind ?? "vehicle", correct, recognized, ms.ToArray(), meta);
            return Ok(new { e.Id, e.Key, e.Correct, e.Recognized, Changed = e.Correct != e.Recognized, count = _store.List().Count });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("corrections")]
    public IActionResult DeleteCorrection([FromQuery] string key) =>
        Ok(new { deleted = _store.Delete(key), count = _store.List().Count });

    [HttpPost("corrections/clear")]
    public IActionResult ClearCorrections()
    {
        var deleted = _store.Clear();
        // mẫu bị xoá hết → các ảnh trong hàng chờ không còn "xong"
        foreach (var e in _inbox.List().Where(x => x.Done)) _inbox.SetDone(e.Id, false);
        return Ok(new { deleted, count = 0 });
    }

    [HttpGet("corrections/{id}/image")]
    public IActionResult CorrectionImage(string id)
    {
        var path = _store.ImagePath(id);
        return path is null ? NotFound() : PhysicalFile(path, "image/png");
    }

    // ------------------------------------------------------------------ train
    public class TrainRequest
    {
        public int? Epochs { get; set; }
    }

    /// <summary>Bắt đầu dạy lại (chạy nền). Theo dõi bằng <c>GET train/status</c>.</summary>
    [HttpPost("train")]
    public IActionResult Train([FromBody] TrainRequest? req)
    {
        var (ok, message) = _trainer.Start(req?.Epochs);
        return ok ? Ok(new { message }) : Conflict(new { message });
    }

    [HttpGet("train/status")]
    public IActionResult TrainStatus() => Ok(_trainer.GetStatus());

    [HttpPost("train/cancel")]
    public IActionResult TrainCancel() => Ok(new { cancelled = _trainer.Cancel() });

    /// <summary>Xoá kết quả lần train gần nhất (bảng chi tiết từng biển, so sánh model cũ/mới). Không ảnh hưởng model đang dùng và các mẫu đã lưu.</summary>
    [HttpPost("train/clear")]
    public IActionResult TrainClear()
    {
        var (ok, message) = _trainer.ClearResult();
        return ok ? Ok(new { message }) : Conflict(new { message });
    }

    /// <summary>"Vẫn cài bản mới": cài model vừa train dù chỉ số không vượt model đang chạy.</summary>
    [HttpPost("train/install")]
    public IActionResult TrainInstall()
    {
        var (ok, message) = _trainer.InstallLatest();
        return ok ? Ok(new { message }) : Conflict(new { message });
    }
}
