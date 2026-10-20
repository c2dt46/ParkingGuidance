using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Floor;
using ParkingGuidance.Web.Services.Led;
using ParkingGuidance.Web.Services.Parking;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Settings;

namespace ParkingGuidance.Web.Controllers;

/// <summary>Cài đặt card LED theo Form HDCard của PGS: 100 card, bảng vùng Image/Text/Number; vùng Number lấy dữ liệu từ biến MS, biến MC và project tag.</summary>
[ApiController]
[Route("api/system/leds")]
public class LedApiController : ControllerBase
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".dat" };
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private readonly ILedCardService _cards;
    private readonly ILedDataService _data;
    private readonly ILedDispatcher _dispatcher;
    private readonly IPersistenceService _persistence;
    private readonly ISettingsService _settings;

    public LedApiController(ILedCardService cards, ILedDataService data, ILedDispatcher dispatcher,
        IPersistenceService persistence, ISettingsService settings)
    {
        _cards = cards;
        _data = data;
        _dispatcher = dispatcher;
        _persistence = persistence;
        _settings = settings;
    }

    public class LedSettingsDto { public double RequestTimeoutSeconds { get; set; } }

    /// <summary>Thông số cài đặt gửi LED (hiện có: timeout chờ card/SDK phản hồi, giây).</summary>
    [HttpGet("settings")]
    public IActionResult GetLedSettings() =>
        Ok(new { requestTimeoutSeconds = _settings.Current.LedRequestTimeoutSeconds ?? 15 });

    /// <summary>Lưu timeout gửi LED (1–120 giây); có hiệu lực ngay ở yêu cầu kế tiếp, không cần khởi động lại.</summary>
    [HttpPut("settings")]
    public IActionResult SaveLedSettings([FromBody] LedSettingsDto dto)
    {
        if (double.IsNaN(dto.RequestTimeoutSeconds) || double.IsInfinity(dto.RequestTimeoutSeconds))
            return BadRequest(new { message = "Timeout phải là một số." });
        var cur = _settings.Current.Sanitized(); // bản sao, không sửa trực tiếp object đang dùng
        cur.LedRequestTimeoutSeconds = dto.RequestTimeoutSeconds;
        var saved = _settings.Update(cur);
        return Ok(new { requestTimeoutSeconds = saved.LedRequestTimeoutSeconds });
    }

    /// <summary>Danh mục của HDCard: loại card, màu, xám, hiệu ứng, tốc độ, độ trễ, cỡ chữ, font.</summary>
    [HttpGet("catalog")]
    public IActionResult Catalog() => Ok(HdCardCatalog.ToDto());

    /// <summary>Danh sách 100 card (nút "Danh sách card") kèm trạng thái.</summary>
    [HttpGet("cards")]
    public IActionResult Cards()
    {
        var statuses = _dispatcher.GetStatuses();
        return Ok(_cards.GetAll().Select((c, i) => new
        {
            c.CardNo, ip = c.Para_IPAddress, enabled = c.Para_Enable, width = c.Para_Width, height = c.Para_Height,
            model = HdCardCatalog.Models[c.Para_Mode], areaCount = c.Areas.Count, status = statuses[i]
        }));
    }

    [HttpGet("cards/{no:int}")]
    public IActionResult GetCard(int no) =>
        no is < 1 or > HdCardCatalog.CardCount ? NotFound() : Ok(_cards.Get(no));

    /// <summary>Lưu toàn bộ 1 card (thông số + bảng vùng) — giao diện tự lưu sau mỗi thay đổi như Save_1_card/Save_All_card của PGS.</summary>
    [HttpPut("cards/{no:int}")]
    public IActionResult SaveCard(int no, [FromBody] LedCard card)
    {
        var (saved, error) = _cards.Save(no, card);
        return saved is null ? BadRequest(new { message = error }) : Ok(saved);
    }

    /// <summary>Nút "Gửi tới card": ép gửi lại toàn bộ nội dung.</summary>
    [HttpPost("cards/{no:int}/send")]
    public IActionResult Send(int no)
    {
        if (no is < 1 or > HdCardCatalog.CardCount) return NotFound();
        _dispatcher.ForceSend(no);
        return Ok(new { queued = true });
    }

    [HttpPost("cards/{no:int}/ping")]
    public async Task<IActionResult> Ping(int no, CancellationToken ct)
    {
        if (no is < 1 or > HdCardCatalog.CardCount) return NotFound();
        var r = await _dispatcher.PingAsync(no, ct);
        return Ok(new { ok = r.Ok, code = r.Code, error = r.Error, elapsedMs = r.ElapsedMs });
    }

    /// <summary>Dữ liệu động cho giao diện (gọi định kỳ): trạng thái card + giá trị hiện tại của biến MS, biến MC và project tag (khoá = tên).</summary>
    [HttpGet("live")]
    public IActionResult Live()
    {
        var s = _data.GetAll();
        return Ok(new
        {
            transport = _dispatcher.TransportName,
            warning = _dispatcher.TransportWarning,
            statuses = _dispatcher.GetStatuses(),
            ms = s.Ms, mc = s.Mc, tags = s.Tags
        });
    }

    /// <summary>Danh sách để chọn trong vùng Number: biến MS, biến MC, project tag kiểu số (Integer/Boolean/Real), kèm giá trị hiện tại.</summary>
    [HttpGet("data-sources")]
    public IActionResult DataSources()
    {
        var d = _data.GetSources();
        return Ok(new { ms = d.Ms, mc = d.Mc, tags = d.Tags });
    }

    // ---------------- Ảnh cho vùng Image ----------------
    [HttpPost("images")]
    [RequestSizeLimit(MaxImageBytes + 4096)]
    public async Task<IActionResult> UploadImage(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "Chưa chọn file." });
        if (file.Length > MaxImageBytes) return BadRequest(new { message = "Ảnh tối đa 5 MB." });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!ImageExtensions.Contains(ext)) return BadRequest(new { message = "Chỉ nhận ảnh: " + string.Join(", ", ImageExtensions) });

        var dir = Path.Combine(_persistence.DataDirectory, "led-images");
        Directory.CreateDirectory(dir);
        var name = Guid.NewGuid().ToString("N") + ext;
        await using (var fs = System.IO.File.Create(Path.Combine(dir, name)))
            await file.CopyToAsync(fs, ct);
        return Ok(new { path = "led-images/" + name, name = Path.GetFileName(file.FileName) });
    }

    [HttpGet("images/{name}")]
    public IActionResult GetImage(string name)
    {
        if (name != Path.GetFileName(name)) return BadRequest();
        var full = Path.Combine(_persistence.DataDirectory, "led-images", name);
        if (!System.IO.File.Exists(full)) return NotFound();
        var type = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", _ => "application/octet-stream"
        };
        return PhysicalFile(full, type);
    }
}
