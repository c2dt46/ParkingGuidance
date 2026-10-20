using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Persistence;

namespace ParkingGuidance.Web.Services.Settings;

/// <summary>Giữ cài đặt chung trong bộ nhớ, tự tải khi khởi động và tự lưu sau mỗi lần đổi (settings.json).
/// Lần chạy đầu tiên (chưa có file) lấy mặc định từ appsettings.json mục "Detection".</summary>
public class SettingsService : ISettingsService
{
    private const string StateKey = "settings";
    private readonly IPersistenceService _persistence;
    private readonly object _lock = new();
    private volatile SystemSettings _current;

    public SettingsService(IPersistenceService persistence, IConfiguration config)
    {
        _persistence = persistence;

        var saved = _persistence.Load<SystemSettings>(StateKey);
        if (saved is not null)
        {
            // File cũ chưa có LedRequestTimeoutSeconds -> lấy từ appsettings (Led:RequestTimeoutSeconds)
            saved.LedRequestTimeoutSeconds ??= config.GetValue("Led:RequestTimeoutSeconds", 15.0);
            _current = saved.Sanitized();
            _persistence.Save(StateKey, _current);
            return;
        }

        var d = new SystemSettings();
        _current = new SystemSettings
        {
            IntervalSeconds = config.GetValue("Detection:IntervalSeconds", d.IntervalSeconds),
            DefaultMotionThresholdPercent = config.GetValue("Detection:DefaultMotionThresholdPercent", d.DefaultMotionThresholdPercent),
            ConfidenceThreshold = config.GetValue("Detection:ConfidenceThreshold", d.ConfidenceThreshold),
            OpenTimeoutSeconds = config.GetValue("Detection:OpenTimeoutSeconds", d.OpenTimeoutSeconds),
            FrameReadTimeoutSeconds = config.GetValue("Detection:FrameReadTimeoutSeconds", d.FrameReadTimeoutSeconds),
            MaxRetryBackoffSeconds = config.GetValue("Detection:MaxRetryBackoffSeconds", d.MaxRetryBackoffSeconds),
            QueueWarnSeconds = d.QueueWarnSeconds,
            VacancyConfirmFrames = d.VacancyConfirmFrames,
            LedRequestTimeoutSeconds = config.GetValue("Led:RequestTimeoutSeconds", 15.0)
        }.Sanitized();
        _persistence.Save(StateKey, _current);
    }

    public SystemSettings Current => _current;

    public SystemSettings Update(SystemSettings settings)
    {
        lock (_lock)
        {
            _current = settings.Sanitized();
            _persistence.Save(StateKey, _current);
            return _current;
        }
    }
}
