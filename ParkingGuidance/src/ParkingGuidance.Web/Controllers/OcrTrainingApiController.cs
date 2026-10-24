using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Services.Simulation;

namespace ParkingGuidance.Web.Controllers;

/// <summary>
/// Tab "Train" → "Train đọc chuỗi (OCR)": huấn luyện model đọc cả chuỗi biển (không cần tách ký tự) từ các mẫu đã lưu,
/// theo dõi tiến trình và cài model. Train chạy ở tiến trình riêng, ưu tiên thấp, chỉ khi bấm Train.
/// </summary>
[ApiController]
[Route("api/system/ocr-train")]
public class OcrTrainingApiController : ControllerBase
{
    private readonly OcrTrainer _trainer;

    public OcrTrainingApiController(OcrTrainer trainer) => _trainer = trainer;

    public class OcrTrainRequest
    {
        public int? Epochs { get; set; }
        public int? Synth { get; set; }
    }

    /// <summary>Bắt đầu train (chạy nền). Theo dõi bằng <c>GET status</c>.</summary>
    [HttpPost("start")]
    public IActionResult Start([FromBody] OcrTrainRequest? req)
    {
        var (ok, message) = _trainer.Start(req?.Epochs, req?.Synth);
        return ok ? Ok(new { message }) : Conflict(new { message });
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(_trainer.GetStatus());

    [HttpPost("cancel")]
    public IActionResult Cancel() => Ok(new { cancelled = _trainer.Cancel() });

    [HttpPost("clear")]
    public IActionResult Clear()
    {
        var (ok, message) = _trainer.ClearResult();
        return ok ? Ok(new { message }) : Conflict(new { message });
    }

    /// <summary>"Vẫn cài bản mới": cài model OCR vừa train dù chưa đạt ngưỡng tin cậy.</summary>
    [HttpPost("install")]
    public IActionResult Install()
    {
        var (ok, message) = _trainer.InstallLatest();
        return ok ? Ok(new { message }) : Conflict(new { message });
    }
}
