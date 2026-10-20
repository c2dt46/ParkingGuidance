using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Tags;

namespace ParkingGuidance.Web.Controllers;

/// <summary>TASKS › Tags: System tags (chỉ đọc, do luồng nền riêng cập nhật) và Project tags (người dùng khai báo).</summary>
[ApiController]
[Route("api/tasks/tags")]
public class TagsApiController : ControllerBase
{
    private readonly ISystemTagService _system;
    private readonly IProjectTagService _project;

    public TagsApiController(ISystemTagService system, IProjectTagService project)
    {
        _system = system;
        _project = project;
    }

    public class ValueDto { public string? Value { get; set; } }

    private static string Fmt(TagDataType type, long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------- System tags
    [HttpGet("system")]
    public IActionResult GetSystem() => Ok(new
    {
        status = _system.GetStatus(),
        tags = _system.GetAll().Select(i => new
        {
            no = i.Def.No,
            name = i.Def.Name,
            value = Fmt(i.Def.Type, i.Value),
            type = i.Def.Type,
            description = i.Def.Description,
            scope = i.Def.Scope
        })
    });

    // ------------------------------------------------------------ Project tags
    [HttpGet("project")]
    public IActionResult GetProject() => Ok(_project.GetAll().Select((t, i) => new
    {
        no = i + 1, name = t.Name, value = t.Value, type = t.Type, description = t.Description, scope = t.Scope
    }));

    [HttpPost("project")]
    public IActionResult Create([FromBody] ProjectTag dto)
    {
        var (tag, error) = _project.Create(dto);
        return tag is null ? BadRequest(new { message = error }) : Ok(tag);
    }

    [HttpPut("project/{name}")]
    public IActionResult Update(string name, [FromBody] ProjectTag dto)
    {
        var (tag, error) = _project.Update(name, dto);
        if (tag is not null) return Ok(tag);
        return _project.Get(name) is null ? NotFound(new { message = error }) : BadRequest(new { message = error });
    }

    [HttpPut("project/{name}/value")]
    public IActionResult SetValue(string name, [FromBody] ValueDto dto)
    {
        var (tag, error) = _project.SetValue(name, dto.Value);
        if (tag is not null) return Ok(tag);
        return _project.Get(name) is null ? NotFound(new { message = error }) : BadRequest(new { message = error });
    }

    [HttpDelete("project/{name}")]
    public IActionResult Delete(string name) => _project.Delete(name) ? NoContent() : NotFound();

    // ------------------------------------------------- tag dạng bit (Layout)
    /// <summary>
    /// Danh sách tag dạng bit (kiểu Boolean) để chọn cho đối tượng Đèn bit / Công tắc bit của Layout Editor:
    /// System tags (chỉ đọc) và Project tags (đọc/ghi). <c>writable</c> = ghi được từ công tắc.
    /// </summary>
    [HttpGet("bits")]
    public IActionResult GetBits()
    {
        var system = _system.GetAll()
            .Where(i => i.Def.Type == TagDataType.Boolean)
            .Select(i => new { name = i.Def.Name, source = "System", writable = false, description = i.Def.Description, value = i.Value != 0 ? 1 : 0 });
        var project = _project.GetAll()
            .Where(t => t.Type == TagDataType.Boolean)
            .Select(t => new { name = t.Name, source = "Project", writable = true, description = t.Description, value = t.Value == "1" ? 1 : 0 });
        return Ok(system.Concat(project));
    }

    /// <summary>
    /// Giá trị hiện tại (0/1) của các tag bit theo tên, dùng để Layout cập nhật đèn/công tắc gần realtime.
    /// Tag không tồn tại hoặc không phải Boolean được liệt kê trong <c>missing</c>. Nhẹ: chỉ đọc bộ nhớ, tối đa 64 tên mỗi lần.
    /// </summary>
    [HttpGet("bit-values")]
    public IActionResult GetBitValues([FromQuery] string? names)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        var list = (names ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(64);

        foreach (var name in list)
        {
            var sysDef = Array.Find(SystemTagService.Defs, d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (sysDef is not null)
            {
                if (sysDef.Type == TagDataType.Boolean && _system.TryGetValue(name, out var sv)) values[name] = sv != 0 ? 1 : 0;
                else missing.Add(name);
                continue;
            }

            var tag = _project.Get(name);
            if (tag is not null && tag.Type == TagDataType.Boolean) values[name] = tag.Value == "1" ? 1 : 0;
            else missing.Add(name);
        }

        return Ok(new { values, missing });
    }

    // -------------------------------------------------------- giá trị trực tiếp
    /// <summary>Giá trị hiện tại để trang cập nhật (group = system | project). Nhẹ: chỉ tên → giá trị.</summary>
    [HttpGet("live")]
    public IActionResult Live([FromQuery] string group = "system")
    {
        if (group.Equals("project", StringComparison.OrdinalIgnoreCase))
            return Ok(new { values = _project.GetValues() });

        return Ok(new
        {
            values = _system.GetAll().ToDictionary(i => i.Def.Name, i => Fmt(i.Def.Type, i.Value)),
            status = _system.GetStatus()
        });
    }
}
