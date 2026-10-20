using System.Diagnostics;
using System.Runtime.Versioning;

namespace ParkingGuidance.Web.Services.Browser;

/// <summary>
/// Tìm và khởi chạy trình duyệt trên MÁY CHỦ (Windows / macOS / Linux). Không bao giờ chạy lệnh tuỳ ý:
/// chỉ chạy các trình duyệt trong danh sách cố định bên dưới, URL được kiểm tra là http/https và truyền
/// dưới dạng tham số riêng (ArgumentList), không ghép chuỗi lệnh.
/// </summary>
public class BrowserLauncher : IBrowserLauncher
{
    public const string DefaultId = "default";

    private sealed record Def(
        string Id, string Name,
        string WinRelativePath,      // so với ProgramFiles / ProgramFiles(x86) / LocalAppData
        string[] LinuxCommands,      // tên lệnh tìm trong PATH
        string? MacApp);             // tên ứng dụng trong /Applications

    private static readonly Def[] Defs =
    {
        new("chrome",  "Google Chrome",  @"Google\Chrome\Application\chrome.exe",
            new[] { "google-chrome", "google-chrome-stable", "chromium-browser", "chromium" }, "Google Chrome"),
        new("edge",    "Microsoft Edge", @"Microsoft\Edge\Application\msedge.exe",
            new[] { "microsoft-edge", "microsoft-edge-stable" }, "Microsoft Edge"),
        new("firefox", "Mozilla Firefox", @"Mozilla Firefox\firefox.exe",
            new[] { "firefox" }, "Firefox"),
        new("safari",  "Safari",          "",                                   // chỉ có trên macOS
            Array.Empty<string>(), "Safari"),
        new("brave",   "Brave",           @"BraveSoftware\Brave-Browser\Application\brave.exe",
            new[] { "brave-browser", "brave" }, "Brave Browser"),
        new("opera",   "Opera",           @"Programs\Opera\opera.exe",
            new[] { "opera" }, "Opera"),
    };

    /// <summary>Cách chạy 1 trình duyệt đã tìm thấy: file thực thi + các tham số đứng trước URL.</summary>
    private sealed record LaunchSpec(string FileName, string[] ArgsBeforeUrl);

    private readonly ILogger<BrowserLauncher> _logger;

    public BrowserLauncher(ILogger<BrowserLauncher> logger) => _logger = logger;

    public IReadOnlyList<BrowserInfo> GetBrowsers() =>
        Defs.Select(d => new BrowserInfo(d.Id, d.Name, Resolve(d) is not null)).ToList();

    public bool IsKnownBrowser(string? id) =>
        id == DefaultId || Defs.Any(d => d.Id == id);

    public BrowserLaunchResult Open(string? browserId, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new BrowserLaunchResult(false, DefaultId, false, "Địa chỉ không hợp lệ (chỉ hỗ trợ http/https).");

        var def = Defs.FirstOrDefault(d => d.Id == browserId);
        string? why = null;

        if (def is not null)
        {
            var spec = Resolve(def);
            if (spec is null)
            {
                why = $"Không tìm thấy {def.Name} trên máy";
            }
            else
            {
                try
                {
                    var psi = new ProcessStartInfo(spec.FileName) { UseShellExecute = false };
                    foreach (var a in spec.ArgsBeforeUrl) psi.ArgumentList.Add(a);
                    psi.ArgumentList.Add(uri.AbsoluteUri);
                    Process.Start(psi);
                    return new BrowserLaunchResult(true, def.Id, false, $"Đã mở bằng {def.Name}.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không khởi động được {Browser}", def.Name);
                    why = $"Không khởi động được {def.Name}";
                }
            }
        }

        // Dự phòng: trình duyệt mặc định của hệ điều hành
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            var prefix = why is null ? "" : why + " → ";
            return new BrowserLaunchResult(true, DefaultId, def is not null, prefix + "đã mở bằng trình duyệt mặc định.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không mở được trình duyệt mặc định");
            return new BrowserLaunchResult(false, DefaultId, def is not null,
                (why is null ? "" : why + ". ") + "Không mở được trình duyệt mặc định: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Tim trinh duyet theo he dieu hanh
    // ------------------------------------------------------------------
    private static LaunchSpec? Resolve(Def d)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ResolveWindows(d);
            if (OperatingSystem.IsMacOS()) return ResolveMac(d);
            return ResolveLinux(d);
        }
        catch
        {
            return null; // loi doc o dia / registry -> coi nhu khong co
        }
    }

    private static LaunchSpec? ResolveWindows(Def d)
    {
        if (string.IsNullOrEmpty(d.WinRelativePath)) return null;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, d.WinRelativePath);
            if (File.Exists(path)) return new LaunchSpec(path, Array.Empty<string>());
        }

        // Cai o vi tri khac: tra trong "App Paths" cua Windows
        var exe = Path.GetFileName(d.WinRelativePath);
        var viaRegistry = ReadAppPath(exe);
        return viaRegistry is not null ? new LaunchSpec(viaRegistry, Array.Empty<string>()) : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadAppPathCore(string exeName)
    {
        var subKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}";
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(subKey);
            if (key?.GetValue(null) is string p)
            {
                p = p.Trim('"');
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static string? ReadAppPath(string exeName) =>
        OperatingSystem.IsWindows() ? ReadAppPathCore(exeName) : null;

    private static LaunchSpec? ResolveMac(Def d)
    {
        if (d.MacApp is null) return null;
        var dirs = new[]
        {
            "/Applications",
            "/System/Applications",
            "/System/Volumes/Preboot/Cryptexes/App/System/Applications", // Safari tren cac ban macOS moi
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
        };
        foreach (var dir in dirs)
            if (Directory.Exists(Path.Combine(dir, d.MacApp + ".app")))
                return new LaunchSpec("/usr/bin/open", new[] { "-a", d.MacApp });
        return null;
    }

    private static LaunchSpec? ResolveLinux(Def d)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var cmd in d.LinuxCommands)
            foreach (var dir in dirs)
            {
                var full = Path.Combine(dir, cmd);
                if (File.Exists(full)) return new LaunchSpec(full, Array.Empty<string>());
            }
        return null;
    }
}
