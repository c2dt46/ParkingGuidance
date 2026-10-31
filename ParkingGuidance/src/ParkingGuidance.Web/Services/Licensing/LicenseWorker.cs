namespace ParkingGuidance.Web.Services.Licensing;

/// <summary>
/// Chương trình chạy ngầm tính thời gian sử dụng. Chỉ làm việc rất nhẹ: mỗi 15 giây cộng thời gian vào bộ đếm
/// (phép cộng trong bộ nhớ), ~60 giây ghi file 1 lần, ~30 phút đọc lại số seri phần cứng 1 lần (WMI, trên luồng
/// thread-pool riêng). Mọi lỗi được chặn tại đây — không bao giờ ảnh hưởng camera / LED / cảm biến / web.
/// </summary>
public sealed class LicenseWorker : BackgroundService
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HardwareEvery = TimeSpan.FromMinutes(30);

    private readonly LicenseService _service;
    private readonly ILogger<LicenseWorker> _log;

    public LicenseWorker(LicenseService service, ILogger<LicenseWorker> log)
    {
        _service = service;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();                     // không chặn quá trình khởi động ứng dụng

        try
        {
            await _service.RefreshHardwareAsync(stoppingToken);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log.LogWarning(ex, "Đọc phần cứng lần đầu lỗi."); }

        var lastHardware = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TickEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _service.Tick();

                    if (DateTime.UtcNow - lastHardware >= HardwareEvery)
                    {
                        lastHardware = DateTime.UtcNow;
                        await _service.RefreshHardwareAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Vòng đếm thời gian bản quyền lỗi (bỏ qua, chạy tiếp)."); }
            }
        }
        catch (OperationCanceledException) { /* tắt ứng dụng */ }

        try { _service.Tick(); _service.Flush(); }
        catch (Exception ex) { _log.LogWarning(ex, "Không ghi được trạng thái bản quyền khi tắt."); }
    }
}
