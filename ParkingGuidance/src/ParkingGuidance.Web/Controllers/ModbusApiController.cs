using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Modbus;

namespace ParkingGuidance.Web.Controllers;

/// <summary>Hệ thống › Thiết bị › Thiết bị ngoài: khai báo Modbus TCP/IP (Server: cổng sẽ mở · Client: IP + cổng thiết bị).</summary>
[ApiController]
[Route("api/system/modbus")]
public class ModbusApiController : ControllerBase
{
    private readonly IModbusConfigService _cfg;
    private readonly IModbusClientStatusProvider _status;
    private readonly IModbusServerStatusProvider _serverStatus;
    public ModbusApiController(IModbusConfigService cfg, IModbusClientStatusProvider status, IModbusServerStatusProvider serverStatus)
    {
        _cfg = cfg;
        _status = status;
        _serverStatus = serverStatus;
    }

    /// <summary>Trạng thái của các Server (cổng đang mở, số kết nối) và Client (kết nối + gửi) — trang hỏi định kỳ khi đang hiện.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new { clients = _status.GetAll(), servers = _serverStatus.GetAll() });

    [HttpGet]
    public IActionResult Get()
    {
        var c = _cfg.Get();
        return Ok(new { maxEntries = ModbusConfigService.MaxEntries, minIntervalMs = ModbusConfigService.MinIntervalMs, maxIntervalMs = ModbusConfigService.MaxIntervalMs, servers = c.Servers, clients = c.Clients });
    }

    [HttpPut("servers")]
    public IActionResult SaveServers([FromBody] List<ModbusServerEntry> servers)
    {
        var error = _cfg.SaveServers(servers);
        return error is not null ? BadRequest(new { message = error }) : Ok(new { servers = _cfg.Get().Servers });
    }

    [HttpPut("clients")]
    public IActionResult SaveClients([FromBody] List<ModbusClientEntry> clients)
    {
        var error = _cfg.SaveClients(clients);
        return error is not null ? BadRequest(new { message = error }) : Ok(new { clients = _cfg.Get().Clients });
    }
}
