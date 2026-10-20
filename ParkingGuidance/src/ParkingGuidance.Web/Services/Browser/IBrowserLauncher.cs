namespace ParkingGuidance.Web.Services.Browser;

/// <summary>Một trình duyệt mà hệ thống biết cách tìm &amp; mở.</summary>
public record BrowserInfo(string Id, string Name, bool Installed);

/// <summary>Kết quả mở trình duyệt. <paramref name="UsedBrowser"/> = mã trình duyệt thực sự được mở ("default" nếu dùng mặc định).</summary>
public record BrowserLaunchResult(bool Success, string UsedBrowser, bool FellBackToDefault, string Message);

public interface IBrowserLauncher
{
    /// <summary>Danh sách trình duyệt hỗ trợ (kèm cờ đã cài trên máy chủ hay chưa).</summary>
    IReadOnlyList<BrowserInfo> GetBrowsers();

    /// <summary>Mã hợp lệ (chrome, edge, firefox, safari, brave, opera hoặc "default").</summary>
    bool IsKnownBrowser(string? id);

    /// <summary>
    /// Mở <paramref name="url"/> bằng trình duyệt <paramref name="browserId"/>. Nếu trình duyệt đó không tồn tại
    /// (hoặc không khởi động được) thì mở bằng trình duyệt mặc định của hệ điều hành.
    /// </summary>
    BrowserLaunchResult Open(string? browserId, string url);
}
