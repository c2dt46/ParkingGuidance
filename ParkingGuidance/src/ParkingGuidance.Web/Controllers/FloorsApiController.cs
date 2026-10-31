using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.CameraRegistry;
using ParkingGuidance.Web.Services.Floor;
using ParkingGuidance.Web.Services.Layout;
using ParkingGuidance.Web.Services.Parking;

namespace ParkingGuidance.Web.Controllers;

[ApiController]
[Route("api/floors")]
public class FloorsApiController : ControllerBase
{
    private readonly IFloorService _floors;
    private readonly IWebHostEnvironment _env;
    private readonly ILayoutService _layout;
    private readonly IParkingService _parking;
    private readonly ICameraRegistryService _cameras;

    public FloorsApiController(IFloorService floors, IWebHostEnvironment env,
        ILayoutService layout, IParkingService parking, ICameraRegistryService cameras)
    {
        _floors = floors;
        _env = env;
        _layout = layout;
        _parking = parking;
        _cameras = cameras;
    }

    public class FloorDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    [HttpGet]
    public IActionResult GetAll() => Ok(_floors.GetFloors());

    [HttpPost]
    public IActionResult Add([FromBody] FloorDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Id))
            return BadRequest(new { message = "Can nhap ma mat bang (vi du: B2)." });

        var id = dto.Id.Trim();
        var name = string.IsNullOrWhiteSpace(dto.Name) ? id : dto.Name.Trim();
        return Ok(_floors.AddFloor(id, name));
    }

    [HttpPut("{id}")]
    public IActionResult Rename(string id, [FromBody] FloorDto dto)
    {
        var floor = _floors.RenameFloor(id, string.IsNullOrWhiteSpace(dto.Name) ? id : dto.Name.Trim());
        return floor is null ? NotFound(new { message = "Khong tim thay mat bang." }) : Ok(floor);
    }

    /// <summary>Đặt làm mặt bằng khởi động (mặt bằng được mở đầu tiên khi vào trang Giám sát). Chỉ 1 mặt bằng duy nhất.</summary>
    [HttpPut("{id}/startup")]
    public IActionResult SetStartup(string id)
    {
        var floor = _floors.SetStartup(id);
        return floor is null ? NotFound(new { message = "Khong tim thay mat bang." }) : Ok(floor);
    }

    /// <summary>Xoá mặt bằng VÀ mọi dữ liệu gắn với nó (đối tượng bản đồ, ô đỗ/phiên đỗ, camera gán tầng, ảnh nền, ảnh upload),
    /// để khi tạo lại cùng mã thì như một mặt bằng hoàn toàn mới.</summary>
    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        if (!_floors.DeleteFloor(id))
            return NotFound(new { message = "Khong tim thay mat bang." });

        var layoutImages = _layout.RemoveFloor(id);
        _parking.RemoveFloorData(id);
        _cameras.ClearFloor(id);

        DeleteUploadedFiles(id, layoutImages);
        return NoContent();
    }

    /// <summary>Xoá file ảnh nền của mặt bằng (uploads/floors/{id}_{ticks}.ext) và ảnh upload của các đối tượng đã xoá (uploads/layout/...).
    /// Chỉ đụng tới file nằm trong thư mục uploads; lỗi xoá file không làm hỏng việc xoá mặt bằng.</summary>
    private void DeleteUploadedFiles(string floorId, IEnumerable<string> layoutImageUrls)
    {
        try
        {
            var floorsDir = Path.Combine(_env.WebRootPath, "uploads", "floors");
            if (Directory.Exists(floorsDir))
            {
                var rx = new Regex("^" + Regex.Escape(floorId) + @"_\d+\.[A-Za-z0-9]+$", RegexOptions.IgnoreCase);
                foreach (var file in Directory.GetFiles(floorsDir).Where(f => rx.IsMatch(Path.GetFileName(f))))
                    TryDelete(file);
            }

            var uploadsRoot = Path.GetFullPath(Path.Combine(_env.WebRootPath, "uploads")) + Path.DirectorySeparatorChar;
            foreach (var url in layoutImageUrls.Distinct())
            {
                if (!url.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.GetFullPath(Path.Combine(_env.WebRootPath, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
                if (full.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase)) TryDelete(full);
            }
        }
        catch { /* don dep file la phu, bo qua */ }
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch { /* file dang duoc dung / khong co quyen: bo qua */ }
    }

    /// <summary>Tải lên ảnh nền (sơ đồ mặt bằng thực tế) cho 1 mặt bằng, dùng làm nền cho Layout Editor.</summary>
    [HttpPost("{id}/background")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> UploadBackground(string id, IFormFile image)
    {
        if (image is null || image.Length == 0)
            return BadRequest(new { message = "Thieu file anh." });

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "floors");
        Directory.CreateDirectory(uploadsDir);

        var ext = Path.GetExtension(image.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
        var fileName = $"{id}_{DateTime.Now.Ticks}{ext}";
        var filePath = Path.Combine(uploadsDir, fileName);

        await using (var stream = System.IO.File.Create(filePath))
        {
            await image.CopyToAsync(stream);
        }

        var url = $"/uploads/floors/{fileName}";
        var floor = _floors.SetBackgroundImage(id, url);
        return floor is null ? NotFound(new { message = "Khong tim thay mat bang." }) : Ok(floor);
    }

    [HttpDelete("{id}/background")]
    public IActionResult DeleteBackground(string id)
    {
        var floor = _floors.SetBackgroundImage(id, null);
        return floor is null ? NotFound(new { message = "Khong tim thay mat bang." }) : Ok(floor);
    }
}
