using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace ParkingGuidance.Web.Controllers;

/// <summary>
/// Nút "Thoát ứng dụng" ở góc dưới-phải màn hình Chạy: tắt chương trình (máy chủ).
/// Mọi tài khoản đã đăng nhập đều dùng được, nhưng chỉ khi thao tác trực tiếp trên máy chủ
/// (máy khác truy cập qua mạng không được tắt chương trình đang chạy).
/// </summary>
[ApiController]
[Route("api/system/app")]
public class AppControlApiController : ControllerBase
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AppControlApiController> _log;

    public AppControlApiController(IHostApplicationLifetime lifetime, ILogger<AppControlApiController> log)
    {
        _lifetime = lifetime;
        _log = log;
    }

    [HttpPost("exit")]
    public IActionResult Exit()
    {
        if (!IsLocalRequest())
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Chỉ tắt được ứng dụng khi thao tác trực tiếp trên máy chủ." });

        _log.LogWarning("Tài khoản {User} bấm Thoát ứng dụng — tắt chương trình.", User.Identity?.Name);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);                 // cho phản hồi kịp gửi về trình duyệt
            _lifetime.StopApplication();
        });
        return Ok(new { message = "Ứng dụng đang tắt." });
    }

    private bool IsLocalRequest()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is null) return true;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;

        var local = HttpContext.Connection.LocalIpAddress;
        if (local is not null && local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        return local is not null && remote.Equals(local);
    }
}
