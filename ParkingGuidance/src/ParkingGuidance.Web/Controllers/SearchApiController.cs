using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Services.Parking;

namespace ParkingGuidance.Web.Controllers;

[ApiController]
[Route("api/search")]
public class SearchApiController : ControllerBase
{
    private readonly IParkingService _parking;
    public SearchApiController(IParkingService parking) => _parking = parking;

    [HttpGet]
    public IActionResult Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return BadRequest(new { message = "Nhap it nhat 2 ky tu de tim kiem." });

        return Ok(_parking.Search(q));
    }
}
