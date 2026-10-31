namespace ParkingGuidance.Web.Services.Licensing;

/// <summary>
/// Khi giấy phép KHÔNG dùng được (chưa kích hoạt / hết hạn / lệch phần cứng / hỏng), người dùng cấp 2 và 3 chỉ vào được
/// trang Giấy phép (để tạo yêu cầu, kích hoạt) và trang đăng nhập. Cấp 0 và 1 không bao giờ bị kiểm tra.
/// Khi giấy phép tốt, middleware chỉ đọc 1 biến bool rồi chuyển tiếp — không tốn thời gian đáng kể.
/// </summary>
public sealed class LicenseGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILicenseService _license;

    public LicenseGateMiddleware(RequestDelegate next, ILicenseService license)
    {
        _next = next;
        _license = license;
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        if (_license.IsOperational) return _next(ctx);

        var user = ctx.User;
        if (user.Identity?.IsAuthenticated != true || AccessLevel.IsPrivileged(user)) return _next(ctx);
        if (IsAllowed(ctx.Request.Path)) return _next(ctx);

        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs") || !HttpMethods.IsGet(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            return ctx.Response.WriteAsync("{\"message\":\"Chưa có giấy phép hợp lệ.\",\"license\":false}");
        }

        ctx.Response.Redirect("/System/License");
        return Task.CompletedTask;
    }

    private static bool IsAllowed(PathString path) =>
        path.StartsWithSegments("/System/License", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/api/license", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/api/system/ui-prefs", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/Login", StringComparison.OrdinalIgnoreCase);
}
