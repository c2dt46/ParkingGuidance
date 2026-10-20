using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Camera;

namespace ParkingGuidance.Web.Controllers;

/// <summary>Hệ thống › Camera › tab "Dữ liệu": các biến MC (mỗi biến đếm 1 trạng thái trên các vị trí đỗ được chỉ định).</summary>
[ApiController]
[Route("api/system/camera-variables")]
public class CameraVariablesApiController : ControllerBase
{
    private readonly ICameraVariableService _vars;
    public CameraVariablesApiController(ICameraVariableService vars) => _vars = vars;

    public class SaveCameraVariableDto : CameraVariable
    {
        /// <summary>Tên biến đang sửa.</summary>
        public string? OriginalName { get; set; }
    }

    /// <summary>Danh sách biến kèm giá trị hiện tại (trang gọi định kỳ khi đang mở tab Dữ liệu). Không kèm phần chọn chi tiết để nhẹ.</summary>
    [HttpGet]
    public IActionResult List() => Ok(new { maxVariables = CameraVariableService.MaxVariables, variables = _vars.GetAll() });

    /// <summary>Camera + các vị trí đỗ đã khoanh vùng (để chọn khi sửa biến).</summary>
    [HttpGet("catalog")]
    public IActionResult Catalog() => Ok(_vars.GetCatalog());

    /// <summary>1 biến đầy đủ (kèm danh sách vị trí) để sửa.</summary>
    [HttpGet("{name}")]
    public IActionResult Get(string name)
    {
        var v = _vars.Get(name);
        return v is null ? NotFound(new { message = $"Không tìm thấy biến '{name}'." }) : Ok(v);
    }

    /// <summary>Thêm ngay 1 biến trống (tên tự đặt MC0, MC1… lấp chỗ trống nhỏ nhất).</summary>
    [HttpPost("add")]
    public IActionResult Add()
    {
        var (v, error) = _vars.Add();
        return error is not null ? BadRequest(new { message = error }) : Ok(v);
    }

    /// <summary>Sửa 1 biến (chọn vị trí, mô tả, trạng thái đếm nếu chưa xác định). Tên không đổi được.</summary>
    [HttpPost]
    public IActionResult Save([FromBody] SaveCameraVariableDto dto)
    {
        var original = string.IsNullOrWhiteSpace(dto.OriginalName) ? null : dto.OriginalName.Trim();
        if (original is null) return BadRequest(new { message = "Thiếu tên biến cần sửa." });
        var (v, error) = _vars.Save(original, dto);
        return error is not null ? BadRequest(new { message = error }) : Ok(v);
    }

    [HttpDelete("{name}")]
    public IActionResult Delete(string name) =>
        _vars.Delete(name) ? Ok(new { deleted = true }) : NotFound(new { message = $"Không tìm thấy biến '{name}'." });
}
