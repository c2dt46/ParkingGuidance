using System.Diagnostics;
using ParkingGuidance.Web.Services.Settings;

namespace ParkingGuidance.Web.Services.Camera;

/// <summary>
/// HÀNG ĐỢI xử lý AI dùng chung cho TOÀN hệ thống: mọi công việc nặng (YOLO + OCR) đều xếp hàng ở đây và
/// được 1 luồng riêng lần lượt xử lý. Nhờ vậy:
///   • Việc lấy ảnh của các camera (nhẹ, chạy song song, độc lập) KHÔNG bị chặn bởi AI đang chạy, và
///     camera lỗi/treo không làm chậm camera khác;
///   • AI chỉ chạy 1 việc tại 1 thời điểm (YOLO/Tesseract vốn dùng chung tài nguyên) nên không bị quá tải CPU;
///   • Thứ tự hợp lý: việc người dùng đang chờ (nút "Nhận diện ngay"/"Chạy thử") được làm TRƯỚC (ưu tiên cao);
///     các camera nền xếp theo thứ tự đến trước làm trước, mỗi camera tối đa 1 việc đang chờ — khung hình
///     mới hơn của cùng camera sẽ THAY THẾ khung cũ chưa kịp xử lý (không dồn ứ ảnh cũ) và giữ nguyên
///     vị trí xếp hàng, nên camera đông tải không "chiếm hết lượt" của camera khác.
/// </summary>
public sealed class AiJobScheduler : BackgroundService
{
    /// <summary>Hàng đợi việc mô phỏng đã đầy (xem <see cref="RunLowPriorityAsync{T}"/>).</summary>
    public sealed class LowQueueFullException : Exception
    {
        public LowQueueFullException() : base("Hàng đợi mô phỏng đang đầy.") { }
    }

    private sealed class Job
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required DateTime EnqueuedUtc { get; init; }
        public required Func<CancellationToken, Task> Work { get; set; }
        public Action? Discard { get; set; }

        /// <summary>Việc ƯU TIÊN THẤP NHẤT (tab Mô phỏng): chỉ chạy khi không còn việc của camera/người dùng, không tính vào thống kê camera.</summary>
        public bool Low { get; init; }
    }

    private readonly object _lock = new();
    private readonly LinkedList<Job> _interactive = new();
    private readonly LinkedList<Job> _background = new();
    private readonly LinkedList<Job> _low = new();      // việc mô phỏng: sau cùng, không bao giờ chen trước camera thật
    private const int MaxLowQueue = 8;
    private readonly Dictionary<string, Job> _pendingBackground = new();
    private readonly SemaphoreSlim _signal = new(0);

    private readonly ISettingsService _settings;
    private readonly ILogger<AiJobScheduler> _logger;

    private Job? _running;
    private DateTime _runningSinceUtc;
    private long _processed;
    private long _coalesced;
    private double _lastDurationMs;
    private DateTime _lastWarnUtc = DateTime.MinValue;

    public AiJobScheduler(ISettingsService settings, ILogger<AiJobScheduler> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Xếp 1 việc nền cho camera <paramref name="key"/>. Nếu camera này đã có việc đang CHỜ (chưa chạy),
    /// việc mới thay thế việc cũ (việc cũ được <paramref name="discard"/> để giải phóng khung hình) nhưng giữ
    /// nguyên lượt xếp hàng. Trả về true nếu đã thay thế việc cũ.</summary>
    public bool EnqueueBackground(string key, string label, Func<CancellationToken, Task> work, Action discard)
    {
        Action? oldDiscard = null;
        bool replaced;
        lock (_lock)
        {
            if (_pendingBackground.TryGetValue(key, out var existing))
            {
                oldDiscard = existing.Discard;
                existing.Work = work;
                existing.Discard = discard;
                replaced = true;
                _coalesced++;
            }
            else
            {
                var job = new Job { Key = key, Label = label, EnqueuedUtc = DateTime.UtcNow, Work = work, Discard = discard };
                _pendingBackground[key] = job;
                _background.AddLast(job);
                replaced = false;
            }
        }

        try { oldDiscard?.Invoke(); } catch { /* giai phong khung cu, loi khong quan trong */ }
        if (!replaced) _signal.Release();
        return replaced;
    }

    /// <summary>Chạy 1 việc ƯU TIÊN CAO (người dùng đang chờ kết quả): xếp trước mọi việc nền, nhưng không
    /// cắt ngang việc đang chạy dở. Ném lại lỗi của việc cho người gọi.</summary>
    public Task<T> RunInteractiveAsync<T>(string label, Func<CancellationToken, Task<T>> work, CancellationToken callerCt)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new Job
        {
            Key = "interactive:" + Guid.NewGuid().ToString("N"),
            Label = label,
            EnqueuedUtc = DateTime.UtcNow,
            Work = async ct =>
            {
                if (callerCt.IsCancellationRequested) { tcs.TrySetCanceled(callerCt); return; }
                try { tcs.TrySetResult(await work(callerCt.CanBeCanceled ? callerCt : ct)); }
                catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }
        };

        lock (_lock) _interactive.AddLast(job);
        _signal.Release();
        return tcs.Task;
    }

    /// <summary>
    /// Chạy 1 việc ƯU TIÊN THẤP NHẤT (tab Mô phỏng): chỉ được chạy khi hàng đợi ưu tiên và hàng đợi camera nền đều TRỐNG,
    /// nên mô phỏng không bao giờ làm chậm việc nhận diện thật (tối đa chờ nốt 1 việc đang chạy dở).
    /// • Huỷ (<paramref name="callerCt"/>) hoặc chờ quá <paramref name="maxWait"/> khi CHƯA chạy: việc bị gỡ khỏi hàng đợi, không tốn tài nguyên.
    /// • Đã bắt đầu chạy thì chạy tới cùng và KHÔNG nhận tín hiệu huỷ — người gọi còn dùng ảnh (Mat) cho tới khi xong.
    /// • Hàng đợi mô phỏng đầy (<see cref="MaxLowQueue"/>) → ném <see cref="LowQueueFullException"/>.
    /// </summary>
    public Task<T> RunLowPriorityAsync<T>(string label, Func<T> work, TimeSpan maxWait, CancellationToken callerCt)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new Job
        {
            Key = "low:" + Guid.NewGuid().ToString("N"),
            Label = label,
            EnqueuedUtc = DateTime.UtcNow,
            Low = true,
            Work = _ =>
            {
                try { tcs.TrySetResult(work()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
                return Task.CompletedTask;
            }
        };

        lock (_lock)
        {
            if (_low.Count >= MaxLowQueue)
                throw new LowQueueFullException();
            _low.AddLast(job);
        }

        // Huỷ / quá hạn khi CHƯA chạy -> gỡ khỏi hàng đợi. Nếu đã bị lấy ra chạy thì để nó chạy xong.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        cts.CancelAfter(maxWait);
        var reg = cts.Token.Register(() =>
        {
            bool removed;
            lock (_lock) removed = _low.Remove(job);
            if (!removed) return;
            if (callerCt.IsCancellationRequested) tcs.TrySetCanceled(callerCt);
            else tcs.TrySetException(new TimeoutException("Chờ AI rảnh quá lâu."));
        });
        tcs.Task.ContinueWith(_ => { reg.Dispose(); cts.Dispose(); }, TaskScheduler.Default);

        _signal.Release();
        return tcs.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            var job = TakeNext();
            if (job is null) continue;

            var waited = DateTime.UtcNow - job.EnqueuedUtc;
            if (!job.Low && waited.TotalSeconds > _settings.Current.QueueWarnSeconds &&
                (DateTime.UtcNow - _lastWarnUtc).TotalSeconds > 30)
            {
                _lastWarnUtc = DateTime.UtcNow;
                _logger.LogWarning(
                    "Hang doi AI dang qua tai: viec '{Label}' cho {Wait:0}s moi duoc xu ly (con {Pending} viec dang cho). " +
                    "Hay tang chu ky lay anh, tang nguong thay doi anh, hoac giam so camera tu dong.",
                    job.Label, waited.TotalSeconds, PendingCount);
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await job.Work(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loi khi xu ly viec AI '{Label}'", job.Label);
            }
            finally
            {
                sw.Stop();
                lock (_lock)
                {
                    _running = null;
                    if (!job.Low)   // viec mo phong khong tinh vao thong ke hang doi camera
                    {
                        _processed++;
                        _lastDurationMs = sw.Elapsed.TotalMilliseconds;
                    }
                }
            }
        }
    }

    private Job? TakeNext()
    {
        lock (_lock)
        {
            Job? job = null;
            if (_interactive.First is { } i)
            {
                job = i.Value;
                _interactive.RemoveFirst();
            }
            else if (_background.First is { } b)
            {
                job = b.Value;
                _background.RemoveFirst();
                _pendingBackground.Remove(job.Key);
            }
            else if (_low.First is { } l)   // chi khi KHONG con viec nao cua camera / nguoi dung
            {
                job = l.Value;
                _low.RemoveFirst();
            }

            if (job is not null)
            {
                _running = job;
                _runningSinceUtc = DateTime.UtcNow;
            }
            return job;
        }
    }

    private int PendingCount { get { lock (_lock) return _interactive.Count + _background.Count + _low.Count; } }

    public record QueueItem(string Label, double WaitingSeconds, bool Interactive);

    public record QueueSnapshot(
        string? Running, double RunningSeconds, int PendingInteractive, int PendingBackground,
        long Processed, long Coalesced, double LastDurationMs, IReadOnlyList<QueueItem> Waiting);

    public QueueSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var waiting = _interactive.Select(j => new QueueItem(j.Label, (now - j.EnqueuedUtc).TotalSeconds, true))
                .Concat(_background.Select(j => new QueueItem(j.Label, (now - j.EnqueuedUtc).TotalSeconds, false)))
                .Concat(_low.Select(j => new QueueItem(j.Label, (now - j.EnqueuedUtc).TotalSeconds, false)))
                .ToList();
            return new QueueSnapshot(
                _running?.Label, _running is null ? 0 : (now - _runningSinceUtc).TotalSeconds,
                _interactive.Count, _background.Count, _processed, _coalesced, _lastDurationMs, waiting);
        }
    }
}
