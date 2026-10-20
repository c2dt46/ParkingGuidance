using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.TrainTransfer;

namespace ParkingGuidance.Web.Controllers;

/// <summary>
/// Hệ thống › Camera: Export dữ liệu train (tab Train) ra thư mục và Import vào (tab Mô phỏng). Các thư mục nằm trên MÁY CHẠY ỨNG DỤNG
/// (không phải máy của trình duyệt), nên có thêm API duyệt thư mục để người dùng chọn thay vì gõ tay.
/// </summary>
[ApiController]
[Route("api/system/train-transfer")]
public class TrainTransferApiController : ControllerBase
{
    private const int MaxDirs = 500;

    private readonly TrainTransferService _service;
    private readonly IPersistenceService _persistence;
    private readonly ILogger<TrainTransferApiController> _logger;

    public TrainTransferApiController(TrainTransferService service, IPersistenceService persistence, ILogger<TrainTransferApiController> logger)
    {
        _service = service;
        _persistence = persistence;
        _logger = logger;
    }

    public class TransferDto
    {
        public string? Folder { get; set; }
        public List<string>? Kinds { get; set; }
        public bool IncludeModels { get; set; }
    }

    public class MkdirDto
    {
        public string? Parent { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>Số liệu train hiện có trên máy này + thư mục gợi ý.</summary>
    [HttpGet("summary")]
    public IActionResult Summary()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var def = !string.IsNullOrWhiteSpace(docs) ? docs : Path.Combine(_persistence.DataDirectory, "exports");
        return Ok(new { kinds = _service.LocalSummary(), defaultFolder = def });
    }

    /// <summary>Liệt kê thư mục con của <paramref name="path"/> (rỗng = ổ đĩa / thư mục thường dùng). Thư mục chứa gói train được đánh dấu.</summary>
    [HttpGet("browse")]
    public IActionResult Browse([FromQuery] string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var roots = new List<object>();
            foreach (var (name, sf) in new[] { ("Tài liệu", Environment.SpecialFolder.MyDocuments), ("Desktop", Environment.SpecialFolder.DesktopDirectory), ("Thư mục người dùng", Environment.SpecialFolder.UserProfile) })
            {
                var p = Environment.GetFolderPath(sf);
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(new { name, path = p, isPackage = false });
            }
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                    if (d.IsReady) roots.Add(new { name = d.Name, path = d.RootDirectory.FullName, isPackage = false });
            }
            catch { /* bo qua o dia loi */ }
            return Ok(new { path = "", parent = (string?)null, dirs = roots, isPackage = false });
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
                    dirs.Add(new { name = Path.GetFileName(d), path = d, isPackage = System.IO.File.Exists(Path.Combine(d, TrainTransferService.ManifestName)) });
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
            parent = Directory.GetParent(full)?.FullName ?? "",     // "" = quay về danh sách ổ đĩa
            dirs,
            isPackage = System.IO.File.Exists(Path.Combine(full, TrainTransferService.ManifestName))
        });
    }

    /// <summary>Tạo thư mục con mới (để chọn nơi export).</summary>
    [HttpPost("mkdir")]
    public IActionResult Mkdir([FromBody] MkdirDto dto)
    {
        if (!TrainTransferService.TryFullPath(dto?.Parent, out var parent, out var err)) return BadRequest(new { message = err });
        var name = (dto?.Name ?? "").Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            return BadRequest(new { message = "Tên thư mục không hợp lệ." });
        if (!Directory.Exists(parent)) return NotFound(new { message = "Thư mục cha không tồn tại." });
        try
        {
            var full = Path.Combine(parent, name);
            Directory.CreateDirectory(full);
            return Ok(new { path = full });
        }
        catch (Exception ex) { return BadRequest(new { message = "Không tạo được thư mục: " + ex.Message }); }
    }

    /// <summary>Xem trước một gói train trong thư mục (đếm mục, mục nào mới, có model không) trước khi Import.</summary>
    [HttpGet("inspect")]
    public IActionResult Inspect([FromQuery] string? folder)
    {
        var r = _service.Inspect(folder);
        if (!r.Ok) return BadRequest(new { message = r.Message });
        return Ok(new { folder = r.Folder, createdAt = r.CreatedAt, kinds = r.Kinds });
    }

    [HttpPost("export")]
    public IActionResult Export([FromBody] TransferDto dto)
    {
        var r = _service.Export(dto?.Folder, dto?.Kinds, dto?.IncludeModels ?? false);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    [HttpPost("import")]
    public IActionResult Import([FromBody] TransferDto dto)
    {
        var r = _service.Import(dto?.Folder, dto?.Kinds, dto?.IncludeModels ?? false);
        return r.Ok ? Ok(r) : BadRequest(r);
    }
}
