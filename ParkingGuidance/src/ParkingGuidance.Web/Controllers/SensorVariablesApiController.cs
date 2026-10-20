using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Sensor;

namespace ParkingGuidance.Web.Controllers;

/// <summary>Hệ thống › Cảm biến › tab "Dữ liệu": các biến MS (mỗi biến đếm 1 trạng thái trên các cảm biến được chỉ định).</summary>
[ApiController]
[Route("api/system/sensors/variables")]
public class SensorVariablesApiController : ControllerBase
{
    private readonly ISensorVariableService _vars;
    private readonly ISensorService _sensors;

    public SensorVariablesApiController(ISensorVariableService vars, ISensorService sensors)
    {
        _vars = vars;
        _sensors = sensors;
    }

    public class SaveVariableDto : SensorVariable
    {
        /// <summary>Tên biến đang sửa.</summary>
        public string? OriginalName { get; set; }
    }

    /// <summary>Danh sách biến kèm giá trị hiện tại (trang gọi định kỳ khi đang mở tab Dữ liệu). Không kèm phần chọn chi tiết để nhẹ.</summary>
    [HttpGet]
    public IActionResult List() => Ok(new
    {
        sensorType = _sensors.Settings.Type.ToString(),
        keytopCount = _sensors.Settings.KeytopCount,
        maxVariables = SensorVariableService.MaxVariables,
        variables = _vars.GetAll()
    });

    /// <summary>1 biến đầy đủ (kèm phần chọn cảm biến) để sửa.</summary>
    [HttpGet("{name}")]
    public IActionResult Get(string name)
    {
        var v = _vars.Get(name);
        return v is null ? NotFound(new { message = $"Không tìm thấy biến '{name}'." }) : Ok(v);
    }

    /// <summary>Thêm ngay 1 biến trống (tên tự đặt MS0, MS1… lấp chỗ trống nhỏ nhất).</summary>
    [HttpPost("add")]
    public IActionResult Add()
    {
        var (v, error) = _vars.Add();
        return error is not null ? BadRequest(new { message = error }) : Ok(v);
    }

    /// <summary>Sửa 1 biến (chọn cảm biến, mô tả, trạng thái đếm nếu chưa xác định). Tên không đổi được.</summary>
    [HttpPost]
    public IActionResult Save([FromBody] SaveVariableDto dto)
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
