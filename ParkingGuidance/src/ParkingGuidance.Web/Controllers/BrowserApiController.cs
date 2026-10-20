using System.Net;
using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Services.Browser;
using ParkingGuidance.Web.Services.Settings;

namespace ParkingGuidance.Web.Controllers;

/// <summary>Chọn trình duyệt dùng để mở giao diện Giám sát (khởi chạy trên máy chủ). Lựa chọn được lưu vào settings.json.</summary>
[ApiController]
[Route("api/system/browser")]
public class BrowserApiController : ControllerBase
{
    private readonly IBrowserLauncher _launcher;
    private readonly ISettingsService _settings;

    public BrowserApiController(IBrowserLauncher launcher, ISettingsService settings)
    {
        _launcher = launcher;
        _settings = settings;
    }

    public record SelectBrowserDto(string? Browser);
    public record OpenBrowserDto(string? Browser, string? Url);

    /// <summary>Danh sách trình duyệt (đã cài hay chưa) + trình duyệt đang được chọn.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        var list = new List<object> { new { id = BrowserLauncher.DefaultId, name = "Mặc định của hệ thống", installed = true } };
        list.AddRange(_launcher.GetBrowsers().Select(b => (object)new { id = b.Id, name = b.Name, installed = b.Installed }));
        return Ok(new { browsers = list, selected = _settings.Current.PreferredBrowser ?? BrowserLauncher.DefaultId });
    }

    /// <summary>Lưu trình duyệt được chọn (cho phép chọn cả trình duyệt chưa cài: lúc mở sẽ tự dùng trình duyệt mặc định).</summary>
    [HttpPut]
    public IActionResult Select([FromBody] SelectBrowserDto dto)
    {
        var id = (dto.Browser ?? BrowserLauncher.DefaultId).Trim().ToLowerInvariant();
        if (!_launcher.IsKnownBrowser(id)) return BadRequest(new { message = "Trình duyệt không hợp lệ." });

        var s = _settings.Current.Sanitized();
        s.PreferredBrowser = id;
        _settings.Update(s);
        return Ok(new { selected = id });
    }

    /// <summary>Mở trang bằng trình duyệt đã chọn (hoặc theo yêu cầu). Chỉ cho phép thao tác từ chính máy chủ.</summary>
    [HttpPost("open")]
    public IActionResult Open([FromBody] OpenBrowserDto dto)
    {
        if (!IsLocalRequest())
            return StatusCode(403, new { message = "Chỉ mở được trình duyệt khi thao tác trực tiếp trên máy chủ." });

        var id = string.IsNullOrWhiteSpace(dto.Browser)
            ? (_settings.Current.PreferredBrowser ?? BrowserLauncher.DefaultId)
            : dto.Browser.Trim().ToLowerInvariant();
        if (!_launcher.IsKnownBrowser(id)) return BadRequest(new { message = "Trình duyệt không hợp lệ." });

        var url = string.IsNullOrWhiteSpace(dto.Url) ? $"{Request.Scheme}://{Request.Host}/" : dto.Url;
        var r = _launcher.Open(id, url);
        return Ok(new { success = r.Success, usedBrowser = r.UsedBrowser, fellBack = r.FellBackToDefault, message = r.Message });
    }

    private bool IsLocalRequest()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is null) return true; // ket noi noi bo (vi du TestServer)
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;

        var local = HttpContext.Connection.LocalIpAddress;
        if (local is not null && local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        return local is not null && remote.Equals(local);
    }
}
