using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Project;
using ParkingGuidance.Web.Services.TrainTransfer;

namespace ParkingGuidance.Web.Controllers;

/// <summary>Dự án = thư mục gom toàn bộ dữ liệu loại 2 (cảm biến, camera, LED, layout...). Nút LƯU chung, MỞ dự án, DỰ ÁN MỚI, LƯU THÀNH.</summary>
[ApiController]
[Authorize(Roles = "Megatech,DEV,Lic")]      // tài khoản User không được lưu / mở / đổi dự án
[Route("api/project")]
public class ProjectApiController : ControllerBase
{
    private const int MaxDirs = 500;

    private readonly IProjectService _project;
    private readonly IPersistenceService _persistence;
    private readonly ILogger<ProjectApiController> _logger;

    public ProjectApiController(IProjectService project, IPersistenceService persistence, ILogger<ProjectApiController> logger)
    {
        _project = project;
        _persistence = persistence;
        _logger = logger;
    }

    public class PathDto
    {
        public string? Path { get; set; }
        /// <summary>Lưu dự án đang mở trước khi chuyển (giao diện đã hỏi người dùng).</summary>
        public bool SaveFirst { get; set; }
    }

    public class NewDto
    {
        /// <summary>Thư mục cha (đã duyệt chọn).</summary>
        public string? Parent { get; set; }
        /// <summary>Tên dự án = tên thư mục con sẽ tạo trong Parent.</summary>
        public string? Name { get; set; }
        public bool SaveFirst { get; set; }
    }

    public class MkdirDto
    {
        public string? Parent { get; set; }
        public string? Name { get; set; }
    }

    private object State() => new
    {
        current = _project.Current,
        recent = _project.Recent,
        unsaved = _persistence.HasUnsavedProjectChanges,
        systemDirectory = _persistence.DataDirectory,
        defaultParent = JsonPersistenceService.DefaultProjectsRoot(_persistence.DataDirectory)
    };

    private IActionResult Run(Func<object> action)
    {
        try { return Ok(action()); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loi thao tac du an.");
            return StatusCode(500, new { message = "Lỗi không xác định: " + ex.Message });
        }
    }

    /// <summary>Dự án đang mở + danh sách dự án gần đây + thư mục dữ liệu hệ thống.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(State());

    /// <summary>LƯU CHUNG: ghi toàn bộ dữ liệu dự án (cảm biến, camera, LED, layout, mặt bằng, biến, tag, Modbus, bãi xe).</summary>
    [HttpPost("save")]
    public IActionResult Save() => Run(() => { _project.Save(); return State(); });

    [HttpPost("open")]
    public IActionResult Open([FromBody] PathDto dto) => Run(() => { if (dto?.SaveFirst == true) _project.Save(); _project.Open(dto?.Path ?? ""); return State(); });

    [HttpPost("new")]
    public IActionResult New([FromBody] NewDto dto) => Run(() =>
    {
        var (path, name) = ResolveNew(dto);
        if (dto?.SaveFirst == true) _project.Save();
        _project.Create(path, name);
        return State();
    });

    [HttpPost("save-as")]
    public IActionResult SaveAs([FromBody] NewDto dto) => Run(() =>
    {
        var (path, name) = ResolveNew(dto);
        _project.SaveAs(path, name);
        return State();
    });

    private static (string Path, string Name) ResolveNew(NewDto? dto)
    {
        var name = (dto?.Name ?? "").Trim();
        if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new InvalidOperationException("Tên dự án không hợp lệ (không được để trống hoặc chứa \\ / : * ? \" < > |).");
        if (!TrainTransferService.TryFullPath(dto?.Parent, out var parent, out var err)) throw new InvalidOperationException(err);
        if (!Directory.Exists(parent)) throw new InvalidOperationException("Thư mục cha không tồn tại.");
        return (System.IO.Path.Combine(parent, name), name);
    }

    /// <summary>Liệt kê thư mục con của <paramref name="path"/> (rỗng = ổ đĩa / thư mục thường dùng). Thư mục là dự án PGS được đánh dấu.</summary>
    [HttpGet("browse")]
    public IActionResult Browse([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = new List<object>();
            var projects = JsonPersistenceService.DefaultProjectsRoot(_persistence.DataDirectory);
            if (Directory.Exists(projects)) roots.Add(new { name = "Thư mục dự án mặc định", path = projects, isProject = false });
            foreach (var (name, sf) in new[] { ("Tài liệu", Environment.SpecialFolder.MyDocuments), ("Desktop", Environment.SpecialFolder.DesktopDirectory), ("Thư mục người dùng", Environment.SpecialFolder.UserProfile) })
            {
                var p = Environment.GetFolderPath(sf);
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(new { name, path = p, isProject = false });
            }
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                    if (d.IsReady) roots.Add(new { name = d.Name, path = d.RootDirectory.FullName, isProject = false });
            }
            catch { /* bo qua o dia loi */ }
            return Ok(new { path = "", parent = (string?)null, dirs = roots, isProject = false });
        }

        if (!TrainTransferService.TryFullPath(path, out var full, out var err)) return BadRequest(new { message = err });
        if (!Directory.Exists(full)) return NotFound(new { message = "Thư mục không tồn tại." });

        var dirs = new List<object>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(full).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var attr = new DirectoryInfo(d).Attributes;
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    dirs.Add(new { name = System.IO.Path.GetFileName(d), path = d, isProject = System.IO.File.Exists(System.IO.Path.Combine(d, ProjectFiles.Manifest)) });
                    if (dirs.Count >= MaxDirs) break;
                }
                catch { /* thu muc khong truy cap duoc: bo qua */ }
            }
        }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { message = "Không có quyền đọc thư mục này." }); }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }

        return Ok(new
        {
            path = full,
            parent = Directory.GetParent(full)?.FullName ?? "",       // "" = quay về danh sách ổ đĩa
            dirs,
            isProject = System.IO.File.Exists(System.IO.Path.Combine(full, ProjectFiles.Manifest))
        });
    }

    public class PickDto
    {
        public string? Initial { get; set; }
    }

    /// <summary>Mở hộp thoại "Browse For Folder" CỦA WINDOWS trên máy đang chạy chương trình (chương trình chạy localhost nên cũng là máy của bạn).
    /// Trả về { path } hoặc { path: null } nếu bấm Cancel. Dùng PowerShell + WinForms nên không cần đổi cấu hình dự án.</summary>
    [HttpPost("pick-folder")]
    public async Task<IActionResult> PickFolder([FromBody] PickDto? dto, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return BadRequest(new { message = "Hộp thoại chọn thư mục của hệ điều hành chỉ có trên Windows. Hãy chọn trong danh sách thư mục." });

        const string script =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            "Add-Type -AssemblyName System.Windows.Forms\n" +
            "$owner = New-Object System.Windows.Forms.Form\n" +
            "$owner.TopMost = $true; $owner.ShowInTaskbar = $false; $owner.StartPosition = 'CenterScreen'; $owner.Opacity = 0; $owner.Show()\n" +
            "$d = New-Object System.Windows.Forms.FolderBrowserDialog\n" +
            "$d.Description = 'Chọn thư mục'\n" +
            "$d.ShowNewFolderButton = $true\n" +
            "if ($env:PGS_INITIAL -and (Test-Path -LiteralPath $env:PGS_INITIAL)) { $d.SelectedPath = $env:PGS_INITIAL }\n" +
            "if ($d.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { [Console]::Out.Write($d.SelectedPath) }\n" +
            "$owner.Close()\n";

        // Đường dẫn đầy đủ + thư mục làm việc là System32: thư mục chạy chương trình có thể nằm trên ổ ảo (Google Drive...) nên
        // tạo tiến trình với thư mục làm việc đó bị "Access is denied".
        var sys = Environment.SystemDirectory;
        var psExe = System.IO.Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(System.IO.File.Exists(psExe) ? psExe : "powershell.exe")
        {
            WorkingDirectory = sys,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-STA");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        psi.Environment["PGS_INITIAL"] = dto?.Initial ?? "";

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return BadRequest(new { message = "Không mở được hộp thoại chọn thư mục." });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var outTask = proc.StandardOutput.ReadToEndAsync();
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } return Ok(new { path = (string?)null }); }
            var picked = (await outTask).Trim();
            return Ok(new { path = string.IsNullOrEmpty(picked) ? null : picked });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong mo duoc hop thoai chon thu muc.");
            return BadRequest(new { message = "Không mở được hộp thoại chọn thư mục: " + ex.Message });
        }
    }

    /// <summary>Tạo thư mục con mới (để chọn nơi đặt dự án).</summary>
    [HttpPost("mkdir")]
    public IActionResult Mkdir([FromBody] MkdirDto dto)
    {
        if (!TrainTransferService.TryFullPath(dto?.Parent, out var parent, out var err)) return BadRequest(new { message = err });
        var name = (dto?.Name ?? "").Trim();
        if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            return BadRequest(new { message = "Tên thư mục không hợp lệ." });
        if (!Directory.Exists(parent)) return NotFound(new { message = "Thư mục cha không tồn tại." });
        try
        {
            var full = System.IO.Path.Combine(parent, name);
            Directory.CreateDirectory(full);
            return Ok(new { path = full });
        }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }
}
