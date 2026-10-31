using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Services.Licensing;

namespace ParkingGuidance.Web.Controllers;

/// <summary>Giấy phép: tạo yêu cầu, nhận / kích hoạt giấy phép, tạo giấy phép (chỉ cấp 0/1), trạng thái, tắt ứng dụng khi hết hạn.</summary>
[ApiController]
[Route("api/license")]
public class LicenseApiController : ControllerBase
{
    private readonly ILicenseService _license;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<LicenseApiController> _log;

    public LicenseApiController(ILicenseService license, IHostApplicationLifetime lifetime, ILogger<LicenseApiController> log)
    {
        _license = license;
        _lifetime = lifetime;
        _log = log;
    }

    public class LicenseDto { public string? License { get; set; } }
    public class RequestDto { public string? Request { get; set; } }
    public class IssueDto { public string? Request { get; set; } public string? Duration { get; set; } }

    private int Level => AccessLevel.Of(User);
    private bool Privileged => Level <= AccessLevel.Lic;

    private static object Hw(HardwareKind kind, string serial, bool? match = null) =>
        new { kind = kind.ToString(), serial, match };

    // ------------------------------------------------------------------ trạng thái
    /// <summary>Rẻ (đọc bộ nhớ): trang web của cấp 2/3 hỏi mỗi ~60 giây để biết có phải báo hết hạn không.</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var s = _license.GetStatus();
        return Ok(new
        {
            state = s.State,
            operational = s.Operational,
            message = s.Message,
            level = Level,
            bypass = Privileged,           // cấp 0/1: mặc định đã có bản quyền, không bị kiểm tra
            canIssue = Privileged,
            durationCode = s.DurationCode,
            durationLabel = s.DurationLabel,
            totalSeconds = s.TotalSeconds,
            consumedSeconds = s.ConsumedSeconds,
            remainingSeconds = s.RemainingSeconds,   // -1 = vĩnh viễn
            matched = s.Matched,
            required = s.Required,
            activatedUtc = s.ActivatedUtc,
            hardware = s.Hardware.Select(h => Hw(h.Kind, h.Serial, h.Match))
        });
    }

    // ------------------------------------------------------------- tab: tạo yêu cầu
    [HttpPost("request")]
    public async Task<IActionResult> CreateRequest(CancellationToken ct)
    {
        var r = await _license.CreateRequestAsync(ct);
        if (!r.Ok) return BadRequest(new { message = r.Error });
        return Ok(new { request = r.Text, hardware = r.Hardware.Select(h => Hw(h.Kind, h.Serial)) });
    }

    // ---------------------------------------------------------- tab: nhận giấy phép
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] LicenseDto dto, CancellationToken ct)
    {
        var r = await _license.PreviewAsync(dto.License, ct);
        if (!r.Ok) return BadRequest(new { message = r.Error });
        return Ok(new
        {
            durationCode = r.DurationCode,
            durationLabel = r.DurationLabel,
            matched = r.Matched,
            required = r.Required,
            canActivate = r.CanActivate,
            message = r.Message,
            hardware = r.Hardware.Select(h => Hw(h.Kind, h.Serial, h.Match))
        });
    }

    [HttpPost("activate")]
    public IActionResult Activate([FromBody] LicenseDto dto)
    {
        var r = _license.Activate(dto.License);
        if (!r.Ok) return BadRequest(new { message = r.Message });
        _log.LogInformation("Người dùng {User} đã kích hoạt giấy phép.", User.Identity?.Name);
        return Ok(new { message = r.Message });
    }

    // ------------------------------------------------------------ tab: tạo giấy phép
    [HttpPost("request-info")]
    public IActionResult RequestInfo([FromBody] RequestDto dto)
    {
        if (!Privileged) return StatusCode(StatusCodes.Status403Forbidden, new { message = "Chỉ tài khoản cấp 0/1 được dùng chức năng này." });
        var r = _license.InspectRequest(dto.Request);
        if (!r.Ok) return BadRequest(new { message = r.Error });
        return Ok(new { hardware = r.Hardware.Select(h => Hw(h.Kind, h.Serial)) });
    }

    [HttpPost("issue")]
    public IActionResult Issue([FromBody] IssueDto dto)
    {
        if (!Privileged) return StatusCode(StatusCodes.Status403Forbidden, new { message = "Chỉ tài khoản cấp 0/1 được tạo giấy phép." });
        var r = _license.Issue(dto.Request, dto.Duration);
        if (!r.Ok) return BadRequest(new { message = r.Error });
        _log.LogInformation("Tài khoản {User} đã tạo giấy phép {Duration}.", User.Identity?.Name, r.DurationLabel);
        return Ok(new { license = r.Text, durationLabel = r.DurationLabel, hardware = r.Hardware.Select(h => Hw(h.Kind, h.Serial)) });
    }

    // -------------------------------------------------------- hết hạn → tắt ứng dụng
    /// <summary>Nút "Xác nhận" của thông báo hết hạn. Chỉ cấp 2/3 và chỉ khi giấy phép thực sự không dùng được.</summary>
    [HttpPost("shutdown")]
    public IActionResult Shutdown()
    {
        if (Privileged || _license.IsOperational)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Không được phép tắt ứng dụng." });

        _log.LogWarning("Giấy phép không còn dùng được — tắt ứng dụng theo xác nhận của {User}.", User.Identity?.Name);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);                 // cho phản hồi kịp gửi về trình duyệt
            _lifetime.StopApplication();
        });
        return Ok(new { message = "Ứng dụng đang tắt." });
    }
}
