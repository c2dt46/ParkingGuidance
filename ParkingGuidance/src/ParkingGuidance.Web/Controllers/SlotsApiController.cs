using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Parking;

namespace ParkingGuidance.Web.Controllers;

[ApiController]
[Route("api/slots")]
public class SlotsApiController : ControllerBase
{
    private readonly IParkingService _parking;
    public SlotsApiController(IParkingService parking) => _parking = parking;

    [HttpGet]
    public IActionResult GetSlots([FromQuery] string? floor) => Ok(_parking.GetSlots(floor));

    [HttpGet("cameras")]
    public IActionResult GetCameras() => Ok(_parking.GetCameras());

    [HttpGet("leds")]
    public IActionResult GetLeds() => Ok(_parking.GetLedBoards());

    [HttpGet("events")]
    public IActionResult GetEvents([FromQuery] int take = 20) => Ok(_parking.GetRecentEvents(take));

    public class ManualSlotUpdateDto
    {
        public SlotStatus Status { get; set; }
        public string? PlateNumber { get; set; }
        public DateTime? ParkedAt { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Cập nhật trạng thái 1 ô đỗ THỦ CÔNG (không qua AI) — dùng cho Layout Editor để
    /// test giao diện, hoặc để admin sửa tay khi cần. Với trạng thái "Đã đỗ" cần truyền
    /// biển số + thời điểm đỗ; với "Lỗi" có thể truyền mô tả lỗi.
    /// </summary>
    [HttpPut("{slotId}/manual")]
    public IActionResult UpdateManual(string slotId, [FromBody] ManualSlotUpdateDto dto)
    {
        try
        {
            var slot = _parking.UpdateSlotManually(slotId, dto.Status, dto.PlateNumber, dto.ParkedAt, dto.ErrorMessage);
            return Ok(slot);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }
}
