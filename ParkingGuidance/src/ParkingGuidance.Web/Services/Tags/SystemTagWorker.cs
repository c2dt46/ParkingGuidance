namespace ParkingGuidance.Web.Services.Tags;

/// <summary>
/// Luồng nền RIÊNG cập nhật System tags (giờ, ngày, nháy chậm/nhanh...).
///
/// Đảm bảo không ảnh hưởng luồng chính:
///  - không chạy chung với camera / cảm biến / LED / bãi xe, không dùng chung khoá hay hàng đợi nào của chúng;
///  - mọi lỗi trong 1 lượt đều bị chặn tại chỗ (chỉ ghi trạng thái + log thưa), không bao giờ ném ra ngoài để làm dừng host
///    (mặc định .NET dừng cả ứng dụng nếu BackgroundService ném lỗi);
///  - nếu cả vòng lặp hỏng thì tự chờ 5 giây rồi chạy lại;
///  - có công tắc tắt hẳn: appsettings "Tags:SystemEnabled": false.
/// </summary>
public sealed class SystemTagWorker : BackgroundService
{
    private readonly SystemTagService _tags;
    private readonly ILogger<SystemTagWorker> _logger;

    public SystemTagWorker(SystemTagService tags, ILogger<SystemTagWorker> logger)
    {
        _tags = tags;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // trả quyền ngay để việc khởi động ứng dụng không phải chờ luồng này

        if (!_tags.Enabled)
        {
            _logger.LogInformation("System tags đang TẮT theo cấu hình (Tags:SystemEnabled = false).");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_tags.TickMs));
                _tags.MarkRunning(true);
                _logger.LogInformation("System tags chạy (quét {Tick} ms, nháy chậm {Slow} ms, nháy nhanh {Fast} ms).",
                    _tags.TickMs, _tags.BlinkSlowMs, _tags.BlinkFastMs);

                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        _tags.Refresh(DateTime.Now, Environment.TickCount64);
                        _tags.ReportTick();
                    }
                    catch (Exception ex)
                    {
                        var n = _tags.ReportError(ex);
                        if (n == 1 || n % 200 == 0)
                            _logger.LogWarning(ex, "System tags: lỗi khi cập nhật (lần thứ {Count}); bỏ qua lượt này.", n);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _tags.ReportError(ex);
                _logger.LogError(ex, "System tags: vòng lặp bị lỗi, thử chạy lại sau 5 giây. Luồng chính không bị ảnh hưởng.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                _tags.MarkRunning(false);
            }
        }
    }
}
