using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Services.Floor;

namespace ParkingGuidance.Web.Controllers;

[ApiController]
[Route("api/floors")]
public class FloorsApiController : ControllerBase
{
    private readonly IFloorService _floors;
    private readonly IWebHostEnvironment _env;

    public FloorsApiController(IFloorService floors, IWebHostEnvironment env)
    {
        _floors = floors;
        _env = env;
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

    [HttpDelete("{id}")]
    public IActionResult Delete(string id) =>
        _floors.DeleteFloor(id) ? NoContent() : NotFound(new { message = "Khong tim thay mat bang." });

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
