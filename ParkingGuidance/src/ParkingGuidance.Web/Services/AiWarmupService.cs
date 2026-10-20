using ParkingGuidance.Web.Services.Anpr;
using ParkingGuidance.Web.Services.Detection;

namespace ParkingGuidance.Web.Services;

/// <summary>
/// Nap model YOLO + Tesseract ở LUỒNG NỀN ngay sau khi web khởi động. Hai dịch vụ này nạp "lười" (lần dùng đầu
/// tiên), nên nếu không khởi động sẵn thì khung hình/ảnh đầu tiên phải chờ nạp model (vài giây) — và việc nạp
/// đó không làm chậm việc mở web vì chạy tách khỏi luồng khởi động.
/// </summary>
public class AiWarmupService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<AiWarmupService> _logger;

    public AiWarmupService(IServiceProvider sp, ILogger<AiWarmupService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
    {
        try
        {
            _ = _sp.GetRequiredService<IVehicleDetectionService>();
            _ = _sp.GetRequiredService<IPlateRecognitionService>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong nap san duoc model AI (se nap lai khi dung lan dau).");
        }
    }, stoppingToken);
}
