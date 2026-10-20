using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Sensor;

namespace ParkingGuidance.Web.Controllers;

/// <summary>Hệ thống › Cảm biến: cài đặt MGT200 / Keytop, trạng thái từng ZCU – từng cảm biến và công cụ thử khung dữ liệu.</summary>
[ApiController]
[Route("api/system/sensors")]
public class SensorsApiController : ControllerBase
{
    private readonly ISensorService _sensors;

    public SensorsApiController(ISensorService sensors) => _sensors = sensors;

    public class TestFrameDto
    {
        /// <summary>"Pm200" hoặc "Keytop".</summary>
        public string? Type { get; set; }
        public string? Frame { get; set; }
    }

    public class CommandDto
    {
        /// <summary>ZCU 0–31.</summary>
        public int Zcu { get; set; }
        /// <summary>Mã lệnh 1–7 (xem <see cref="SensorProtocol.Pm200.BuildCommand"/>).</summary>
        public int Command { get; set; } = 1;
        /// <summary>Line (CN) 1–5; 9 = tất cả (không dùng cho lệnh 7).</summary>
        public int Channel { get; set; } = 1;
        /// <summary>ID cảm biến 0–31; 99 = tất cả.</summary>
        public int Sensor { get; set; }
        /// <summary>Dữ liệu của lệnh 2 (100–600 cm), 4 (0–200 cm), 6 (0–9).</summary>
        public int Data { get; set; }
    }

    /// <summary>Cài đặt đang áp dụng.</summary>
    [HttpGet("settings")]
    public IActionResult GetSettings() => Ok(_sensors.Settings);

    /// <summary>Lưu cài đặt; có hiệu lực ngay (kết nối cũ được đóng và mở lại theo cài đặt mới).</summary>
    [HttpPut("settings")]
    public IActionResult SaveSettings([FromBody] SensorSettings dto)
    {
        var (saved, error) = _sensors.SaveSettings(dto);
        if (error is not null) return BadRequest(new { message = error });
        return Ok(saved);
    }

    /// <summary>Trạng thái hiện tại: kết nối từng ZCU và trạng thái từng cảm biến (trang gọi định kỳ).</summary>
    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(_sensors.GetSnapshot());

    /// <summary>
    /// Trang "Cài đặt cảm biến" của MGT200 (như PM200Setting trong PGS): gửi 1 lệnh tới 1 cảm biến (hoặc tất cả) qua converter,
    /// hoặc (lệnh 7) đọc khoảng cách đo được của 32 cảm biến trên 1 Line. Trả về khung đã gửi và nội dung ZCU phản hồi.
    /// </summary>
    [HttpPost("command")]
    public async Task<IActionResult> RunCommand([FromBody] CommandDto dto)
    {
        if (_sensors.Settings.Type != SensorType.Pm200)
            return BadRequest(new { message = "Lệnh cài đặt chỉ dùng cho cảm biến MGT200 — hãy chọn loại MGT200 và lưu cài đặt." });
        if (dto.Zcu is < 0 or > 31) return BadRequest(new { message = "ZCU phải từ 0 đến 31." });
        if (dto.Command is < 1 or > 7) return BadRequest(new { message = "Mã lệnh phải từ 1 đến 7." });

        string frame;
        if (dto.Command == 7)
        {
            if (dto.Channel is < 1 or > 5) return BadRequest(new { message = "Lệnh đọc khoảng cách cần chọn Line từ CN 1 đến CN 5." });
            frame = SensorProtocol.Pm200.BuildDistanceRequest(dto.Zcu, dto.Channel);
        }
        else
        {
            if (!(dto.Channel is >= 1 and <= 5) && dto.Channel != 9) return BadRequest(new { message = "Line phải là CN 1–5 hoặc tất cả." });
            if (!(dto.Sensor is >= 0 and <= 31) && dto.Sensor != 99) return BadRequest(new { message = "ID cảm biến phải từ 0 đến 31 hoặc tất cả." });

            int data = 0;
            if (dto.Command == 2)
            {
                if (dto.Data is < 100 or > 600) return BadRequest(new { message = "Khoảng cách mặt đất phải từ 100 đến 600 cm." });
                data = dto.Data;
            }
            else if (dto.Command == 4)
            {
                if (dto.Data is < 0 or > 200) return BadRequest(new { message = "Chiều cao nhận diện phải từ 0 đến 200 cm." });
                data = dto.Data;
            }
            else if (dto.Command == 6)
            {
                if (dto.Data is < 0 or > 9) return BadRequest(new { message = "Chế độ màu cảm biến phải từ 0 đến 9." });
                data = dto.Data;
            }
            frame = SensorProtocol.Pm200.BuildCommand(dto.Zcu, dto.Channel, dto.Sensor, dto.Command, data);
        }

        var sent = frame.Replace("\r", "\\r");
        var result = await _sensors.SubmitPm200CommandAsync(frame, TimeSpan.FromSeconds(10));
        var visible = (result.Reply ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
        if (!result.Ok)
            return Ok(new { ok = false, message = result.Error ?? "Gửi lệnh thất bại.", frame = sent, reply = visible });

        if (dto.Command == 7)
        {
            if (SensorProtocol.Pm200.TryParseDistances(result.Reply, dto.Zcu, out var cm))
                return Ok(new { ok = true, message = "Đã đọc khoảng cách.", frame = sent, reply = visible, distances = cm });
            var len = (result.Reply ?? "").Length;
            return Ok(new
            {
                ok = false,
                message = len == 0 ? "ZCU không phản hồi." : $"Phản hồi không đúng định dạng ({len} ký tự, cần 139 và chứa thẻ M{dto.Zcu:00}0992).",
                frame = sent,
                reply = visible
            });
        }

        var msg = string.IsNullOrEmpty(result.Reply)
            ? "Đã gửi lệnh; ZCU không có phản hồi."
            : $"Đã gửi lệnh; nhận được {result.Reply.Length} ký tự phản hồi.";
        return Ok(new { ok = true, message = msg, frame = sent, reply = visible });
    }

    /// <summary>
    /// Thử phân tích 1 khung dữ liệu (dán từ cổng nối tiếp/công cụ TCP) theo giao thức của loại cảm biến — CHỈ phân tích để xem kết quả,
    /// không đưa vào trạng thái thật.
    /// </summary>
    [HttpPost("test")]
    public IActionResult TestFrame([FromBody] TestFrameDto dto)
    {
        var frame = dto.Frame ?? "";
        if (string.IsNullOrWhiteSpace(frame))
            return BadRequest(new { message = "Hãy dán khung dữ liệu cần thử." });

        var keytop = string.Equals(dto.Type, "Keytop", StringComparison.OrdinalIgnoreCase);
        if (keytop)
        {
            var ok = SensorProtocol.Keytop.TryParse(frame, out var items);
            if (!ok)
                return Ok(new
                {
                    ok = false,
                    message = "Không hợp lệ: gói Keytop gồm các nhóm 4 ký tự dạng (Xs) — X là cảm biến 1–60 ('A'=1 … '|'=60), s là 0/1/2; dài 4–240 ký tự."
                });

            var lines = items.Select(i =>
                $"Cảm biến {i.Sensor}: " + (i.State == SensorProtocol.Occupied ? "có xe" : i.State == SensorProtocol.Error ? "lỗi" : "trống")).ToList();
            return Ok(new { ok = true, message = $"Hợp lệ — {items.Count} cảm biến.", lines });
        }

        // MGT200: người dùng có thể dán kèm "\r" dạng chữ; đổi về ký tự thật cho dễ thử
        frame = frame.Replace("\\r", "\r").Replace("\\n", "\n").Trim('\r', '\n');
        if (!SensorProtocol.Pm200.TryParse(frame, out var states))
            return Ok(new
            {
                ok = false,
                message = $"Không hợp lệ: sau khi bỏ thẻ Mnn và \"099\" khung MGT200 phải dài đúng {SensorProtocol.Pm200.FrameLength} ký tự."
            });

        int occ = states.Count(v => v == SensorProtocol.Occupied);
        int err = states.Count(v => v == SensorProtocol.Error);
        int free = states.Length - occ - err;
        var text = new string(states.Select(v => v == SensorProtocol.Occupied ? '1' : v == SensorProtocol.Error ? '-' : '0').ToArray());
        return Ok(new
        {
            ok = true,
            message = $"Hợp lệ — {states.Length} cảm biến: {free} trống, {occ} có xe, {err} lỗi.",
            lines = new[] { text }
        });
    }
}
