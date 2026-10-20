using Microsoft.AspNetCore.Mvc;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Camera;
using ParkingGuidance.Web.Services.CameraRegistry;
using ParkingGuidance.Web.Services.Persistence;
using ParkingGuidance.Web.Services.Settings;

namespace ParkingGuidance.Web.Controllers;

[ApiController]
[Route("api/system")]
public class SystemSettingsApiController : ControllerBase
{
    private readonly ISettingsService _settings;
    private readonly IPersistenceService _persistence;
    private readonly ICameraRegistryService _registry;
    private readonly AiJobScheduler _scheduler;
    private readonly CameraIngestionService _ingestion;

    public SystemSettingsApiController(
        ISettingsService settings, IPersistenceService persistence, ICameraRegistryService registry,
        AiJobScheduler scheduler, CameraIngestionService ingestion)
    {
        _settings = settings;
        _persistence = persistence;
        _registry = registry;
        _scheduler = scheduler;
        _ingestion = ingestion;
    }

    /// <summary>Cài đặt chung đang áp dụng + thư mục lưu dữ liệu.</summary>
    [HttpGet("settings")]
    public IActionResult GetSettings() => Ok(new { settings = _settings.Current, dataDirectory = _persistence.DataDirectory });

    /// <summary>Lưu cài đặt chung xuống đĩa; có hiệu lực ngay (các vòng lặp nền đọc lại ở lần kế tiếp).</summary>
    [HttpPut("settings")]
    public IActionResult UpdateSettings([FromBody] SystemSettings dto)
    {
        // Trang "Cấu hình chung" không gửi PreferredBrowser -> giữ nguyên lựa chọn trình duyệt đang lưu
        dto.PreferredBrowser ??= _settings.Current.PreferredBrowser;
        // Tương tự: trang này không gửi timeout LED -> giữ nguyên giá trị đã cài ở trang LED
        dto.LedRequestTimeoutSeconds ??= _settings.Current.LedRequestTimeoutSeconds;
        // Tương tự: trang này không gửi giao diện dùng chung -> giữ nguyên
        dto.Ui ??= _settings.Current.Ui;
        // Các thông số biển số: gửi thiếu thì giữ giá trị đang lưu
        dto.PlateBoxConfidence ??= _settings.Current.PlateBoxConfidence;
        dto.CharConfidence ??= _settings.Current.CharConfidence;
        dto.PlateDetector ??= _settings.Current.PlateDetector;
        dto.CharCombine ??= _settings.Current.CharCombine;
        dto.PlateMinOverlap ??= _settings.Current.PlateMinOverlap;
        dto.ProcessMaxSide ??= _settings.Current.ProcessMaxSide;
        dto.PlateReadHeight ??= _settings.Current.PlateReadHeight;
        dto.PlateFullSearch ??= _settings.Current.PlateFullSearch;
        return Ok(new { settings = _settings.Update(dto), dataDirectory = _persistence.DataDirectory });
    }

    /// <summary>Giao diện dùng chung cho mọi máy (các máy ở chế độ "Mọi máy" tự lấy giá trị này mỗi khi mở trang).</summary>
    [HttpGet("ui-prefs")]
    public IActionResult GetUiPrefs() => Ok(_settings.Current.Ui ?? new UiPreferences());

    /// <summary>Lưu giao diện dùng chung xuống đĩa; mọi máy đang ở chế độ "Mọi máy" sẽ nhận ở lần mở/làm mới trang kế tiếp.</summary>
    [HttpPut("ui-prefs")]
    public IActionResult SaveUiPrefs([FromBody] UiPreferences dto)
    {
        var cur = _settings.Current.Sanitized(); // bản sao, không sửa trực tiếp object đang dùng
        cur.Ui = dto;
        return Ok(_settings.Update(cur).Ui);
    }

    /// <summary>Trạng thái hàng đợi AI + trạng thái chạy nền của từng camera (để theo dõi, không cần mở trang camera).</summary>
    [HttpGet("queue")]
    public IActionResult GetQueue()
    {
        var statuses = _ingestion.GetStatuses();
        var now = DateTime.UtcNow;

        var cameras = _registry.GetAll()
            .Where(c => c.IsCommitted)
            .Select(c =>
            {
                var reason = CameraIngestionService.GetIneligibleReason(c);
                statuses.TryGetValue(c.Id, out var st);
                return new
                {
                    c.Id,
                    c.IpAddress,
                    c.Brand,
                    c.Floor,
                    Running = reason is null && st is not null,
                    State = reason is not null ? "Khong chay nen: " + reason : (st?.State ?? "Dang khoi dong"),
                    Failures = st?.Failures ?? 0,
                    LastError = st?.LastError,
                    LastFrameSecondsAgo = st?.LastFrameUtc is { } f ? Math.Round((now - f).TotalSeconds) : (double?)null,
                    LastProcessedSecondsAgo = st?.LastProcessedUtc is { } p ? Math.Round((now - p).TotalSeconds) : (double?)null,
                    LastChangePercent = st?.LastChangePercent,
                    LastProcessMs = st?.LastProcessMs
                };
            })
            .ToList();

        return Ok(new { queue = _scheduler.GetSnapshot(), cameras });
    }
}
