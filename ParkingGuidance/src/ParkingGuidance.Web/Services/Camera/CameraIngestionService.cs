using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using OpenCvSharp;
using ParkingGuidance.Web.Hubs;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.CameraRegistry;
using ParkingGuidance.Web.Services.Detection;
using ParkingGuidance.Web.Services.Parking;
using ParkingGuidance.Web.Services.Settings;

namespace ParkingGuidance.Web.Services.Camera;

/// <summary>Trạng thái chạy của 1 camera nền — để hiển thị trên giao diện (tab Cấu hình chung).</summary>
public class CameraRuntimeStatus
{
    public string CameraId { get; init; } = "";
    public string State { get; set; } = "Khoi dong";
    public int Failures { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? LastFrameUtc { get; set; }
    public double? LastChangePercent { get; set; }
    public DateTime? LastQueuedUtc { get; set; }
    public DateTime? LastProcessedUtc { get; set; }
    public double? LastProcessMs { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Giám sát (supervisor) các camera chạy nền. MỖI camera đủ điều kiện có 1 "công nhân" riêng (CameraWorker) chạy
/// ĐỘC LẬP và LUÔN chạy ngầm — không cần mở trang camera/giao diện. Mỗi chu kỳ của 1 công nhân là TUẦN TỰ:
///   dò TCP + mở kết nối (có timeout) → lấy ảnh mới nhất → so mức thay đổi với ảnh tham chiếu → nếu vượt ngưỡng thì XẾP HÀNG
///   vào <see cref="AiJobScheduler"/> (hàng đợi AI dùng chung, 1 luồng, lần lượt từng việc) → CHỜ AI xử lý xong khung đó →
///   rồi mới tới lần lấy mẫu tiếp theo (không bao giờ có 2 khung của cùng 1 camera đang chờ/đang xử lý).
/// Lỗi/treo ở camera nào chỉ ảnh hưởng camera đó (tự nghỉ theo backoff rồi thử lại): camera mất mạng bị loại bằng dò TCP nhanh,
/// lời gọi chặn của OpenCV chạy ở luồng riêng và chỉ 1 lượt mở tại 1 thời điểm (xem <see cref="VideoCaptureHelper"/>).
///
/// Điều kiện để camera được chạy nền: đã "Thêm vào hệ thống bãi đỗ", có "Nguồn video", ở chế độ Tự động, và đã vẽ ít
/// nhất 1 vùng gắn với 1 ô đỗ. Danh sách camera được so khớp lại mỗi 2 giây nên thêm/sửa/xoá có hiệu lực ngay.
/// </summary>
public class CameraIngestionService : BackgroundService
{
    private readonly ICameraRegistryService _registry;
    private readonly ISettingsService _settings;
    private readonly AiJobScheduler _scheduler;
    private readonly IFrameChangeDetector _changeDetector;
    private readonly ICameraFrameProcessor _processor;
    private readonly IParkingService _parking;
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<CameraIngestionService> _logger;

    /// <summary>Số lần mở/đọc camera lỗi LIÊN TIẾP trước khi các ô của camera chuyển sang trạng thái Lỗi (vàng).</summary>
    private const int ErrorAfterFailures = 2;

    private sealed record WorkerHandle(CancellationTokenSource Cts, Task Task, CameraRuntimeStatus Status);
    private readonly ConcurrentDictionary<string, WorkerHandle> _workers = new();

    public CameraIngestionService(
        ICameraRegistryService registry, ISettingsService settings, AiJobScheduler scheduler,
        IFrameChangeDetector changeDetector, ICameraFrameProcessor processor,
        IParkingService parking, IHubContext<MonitoringHub> hub,
        ILogger<CameraIngestionService> logger)
    {
        _parking = parking;
        _hub = hub;
        _registry = registry;
        _settings = settings;
        _scheduler = scheduler;
        _changeDetector = changeDetector;
        _processor = processor;
        _logger = logger;
    }

    /// <summary>Lý do camera KHÔNG chạy nền (null = đủ điều kiện).</summary>
    public static string? GetIneligibleReason(ManagedCamera c)
    {
        if (!c.IsCommitted) return "Chua them vao he thong bai do";
        if (string.IsNullOrWhiteSpace(c.Zone)) return "Chua gan Zone (khu vuc lap dat trong tang ham)";
        if (string.IsNullOrWhiteSpace(c.VideoSource)) return "Chua khai bao Nguon video";
        if (c.DetectionMode == "Manual") return "Che do Thu cong";
        if (!c.Zones.Any(z => SlotKey.TryNormalizeNumber(z.SlotId, out _))) return "Chua ve vung nhan dien (so thu tu la so nguyen duong)";
        return null;
    }

    public IReadOnlyDictionary<string, CameraRuntimeStatus> GetStatuses() =>
        _workers.ToDictionary(kv => kv.Key, kv => kv.Value.Status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Tra quyen dieu khien cho host NGAY: khoi dong web khong phai cho bat ky camera nao
        await Task.Yield();
        VideoCaptureHelper.EnsureFfmpegTimeout(TimeSpan.FromSeconds(_settings.Current.FrameReadTimeoutSeconds));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Reconcile(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* dang tat server */ }
        finally
        {
            foreach (var w in _workers.Values) w.Cts.Cancel();
            try { await Task.WhenAll(_workers.Values.Select(w => w.Task)).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* dang tat */ }
            _workers.Clear();
        }
    }

    /// <summary>So khớp danh sách camera đủ điều kiện với các công nhân đang chạy: bật cho camera mới đủ điều kiện,
    /// dừng cho camera bị xoá/chuyển Thủ công/mất điều kiện hoặc công nhân đã tự kết thúc.</summary>
    private void Reconcile(CancellationToken stoppingToken)
    {
        var eligible = _registry.GetAll().Where(c => GetIneligibleReason(c) is null).Select(c => c.Id).ToHashSet();

        foreach (var (id, handle) in _workers.ToArray())
        {
            if (!eligible.Contains(id) || handle.Task.IsCompleted)
            {
                handle.Cts.Cancel();
                if (_workers.TryRemove(id, out _))
                    _changeDetector.CommitReference(id); // khong de khung cho chot bi ton lai
            }
        }

        foreach (var id in eligible)
        {
            if (_workers.ContainsKey(id)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var status = new CameraRuntimeStatus { CameraId = id };
            var task = Task.Run(() => RunCameraAsync(id, status, cts.Token));
            _workers[id] = new WorkerHandle(cts, task, status);
            _logger.LogInformation("Bat dau chay nen camera {CameraId}.", id);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Cong nhan cua 1 camera
    // ------------------------------------------------------------------------------------------------

    /// <summary>Quá chừng này giây KHÔNG đọc khung nào (ví dụ chờ hàng đợi AI lâu) thì kết nối RTSP đang giữ bị coi là cũ
    /// (bộ đệm dồn khung cũ) -> đóng và mở lại để chắc chắn lấy được khung MỚI.</summary>
    private const double ReuseConnectionMaxIdleSeconds = 10;

    /// <summary>Nghỉ ngắn khi chưa tới lượt mở kết nối (thiết bị khác đang mở) — KHÔNG tính là lỗi của camera này.</summary>
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(500);

    private async Task RunCameraAsync(string cameraId, CameraRuntimeStatus status, CancellationToken ct)
    {
        VideoCapture? capture = null;
        string source = "";
        int failures = 0;
        bool errorFlagged = false; // cac o cua camera nay dang duoc danh dau Loi (vang)
        DateTime lastReadUtc = DateTime.MinValue;

        try
        {
            // Lech pha ngau nhien de cac camera khong cung don hang doi 1 luc
            await Task.Delay(Random.Shared.Next(0, (int)(_settings.Current.IntervalSeconds * 1000) + 1), ct);

            while (!ct.IsCancellationRequested)
            {
                var cam = _registry.Get(cameraId);
                if (cam is null || GetIneligibleReason(cam) is not null) break;

                var cfg = _settings.Current; // doc moi vong -> doi cai dat co hieu luc ngay
                var interval = TimeSpan.FromSeconds(cfg.IntervalSeconds);
                var cycle = Stopwatch.StartNew();
                TimeSpan? failureDelay = null;   // camera loi that su -> lui dan (backoff)
                TimeSpan? busyDelay = null;      // chua toi luot mo ket noi: khong phai loi cua camera nay
                Task? processing = null;         // viec AI cua khung vua lay: cho xong roi moi lay mau tiep theo

                try
                {
                    if (source != cam.VideoSource)
                    {
                        VideoCaptureHelper.DisposeInBackground(capture);
                        capture = null;
                        source = cam.VideoSource!;
                    }

                    bool isFile = File.Exists(source);

                    // Ket noi giu qua lau khong doc (cho hang doi AI...) -> bo dem da day khung cu: mo lai cho chac chan co khung moi
                    if (capture is not null && !isFile &&
                        (DateTime.UtcNow - lastReadUtc).TotalSeconds > ReuseConnectionMaxIdleSeconds)
                    {
                        VideoCaptureHelper.DisposeInBackground(capture);
                        capture = null;
                    }

                    if (capture is null)
                    {
                        status.State = "Dang ket noi";
                        var open = await VideoCaptureHelper.OpenAsync(source, TimeSpan.FromSeconds(cfg.OpenTimeoutSeconds), ct);
                        if (open.Status == CaptureOpenStatus.Ok)
                        {
                            capture = open.Capture;
                        }
                        else if (open.Status == CaptureOpenStatus.Busy)
                        {
                            status.State = "Cho luot ket noi";
                            busyDelay = BusyRetryDelay;
                        }
                        else
                        {
                            failureDelay = Fail(cameraId, status, ref failures,
                                $"khong mo duoc nguon: {open.Error}", interval, cfg);
                        }
                    }

                    if (capture is not null)
                    {
                        status.State = "Dang lay anh";
                        var read = await VideoCaptureHelper.ReadAsync(
                            capture, TimeSpan.FromSeconds(cfg.FrameReadTimeoutSeconds), latest: !isFile, source: source);

                        if (read.Status == FrameReadStatus.Timeout)
                        {
                            capture = null; // helper tu giai phong capture khi loi goi chan ket thuc
                            failureDelay = Fail(cameraId, status, ref failures,
                                $"khong lay duoc anh trong {cfg.FrameReadTimeoutSeconds:0}s", interval, cfg);
                        }
                        else if (read.Status == FrameReadStatus.Failed)
                        {
                            if (isFile)
                            {
                                capture.Set(VideoCaptureProperties.PosFrames, 0); // file video het: quay lai tu dau
                            }
                            else
                            {
                                VideoCaptureHelper.DisposeInBackground(capture);
                                capture = null;
                                failureDelay = Fail(cameraId, status, ref failures,
                                    "doc khung hinh that bai (mat ket noi?)", interval, cfg);
                            }
                        }
                        else
                        {
                            if (failures > 0)
                            {
                                _logger.LogInformation("Camera {CameraId} da hoat dong tro lai.", cameraId);
                                failures = 0;
                                status.Failures = 0;
                                status.NextAttemptUtc = null;
                                status.LastError = null;
                            }
                            lastReadUtc = DateTime.UtcNow;
                            status.LastFrameUtc = lastReadUtc;
                            processing = HandleFrame(cam, read.Frame!, status, cfg, ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Loi khi xu ly camera {CameraId}", cameraId);
                    status.LastError = ex.Message;
                    failureDelay = Fail(cameraId, status, ref failures, ex.Message, interval, cfg, log: false);
                }

                // Camera loi lien tiep -> cac o cua camera chuyen sang "Loi" (vang); hoat dong lai -> khoi phuc trang thai truoc do
                var slotIds = SlotKeysOf(cam);
                if (failureDelay is not null && failures >= ErrorAfterFailures && !errorFlagged)
                {
                    errorFlagged = true;
                    if (_parking.SetCameraError(slotIds, $"Camera mat ket noi ({status.LastError})"))
                        await NotifySlotsChangedAsync(ct);
                }
                else if (failureDelay is null && busyDelay is null && errorFlagged)
                {
                    errorFlagged = false;
                    if (_parking.RecoverFromError(slotIds))
                        await NotifySlotsChangedAsync(ct);
                }

                // TUAN TU: khung vua lay phai duoc AI xu ly XONG thi camera moi lay mau tiep theo
                // (hang doi AI dung chung chay lan luot tung viec; moi camera luon chi co toi da 1 khung dang cho/dang xu ly)
                if (processing is not null)
                {
                    await processing.WaitAsync(ct);
                    status.State = "Da xu ly xong";
                }

                var delay = failureDelay ?? busyDelay ?? (interval - cycle.Elapsed);
                if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException) { /* dung cong nhan */ }
        finally
        {
            VideoCaptureHelper.DisposeInBackground(capture);
            if (errorFlagged)
            {
                // Cong nhan dung (camera bi xoa / chuyen che do Thu cong / het dieu kien): tra cac o ve trang thai truoc khi loi
                var last = _registry.Get(cameraId);
                if (last is not null &&
                    _parking.RecoverFromError(SlotKeysOf(last)))
                    _ = NotifySlotsChangedAsync(CancellationToken.None);
            }
            status.State = "Da dung";
            _logger.LogInformation("Da dung chay nen camera {CameraId}.", cameraId);
        }
    }

    /// <summary>So sánh khung với ảnh tham chiếu; nếu vượt ngưỡng thì chuyển khung cho hàng đợi AI (hàng đợi sở hữu khung
    /// và tự giải phóng). Nếu không, giải phóng ngay. Trả về Task hoàn thành khi AI đã xử lý xong khung này
    /// (hoặc khung bị huỷ/lỗi); trả về null nếu không cần xử lý (ảnh chưa đổi).</summary>
    private Task? HandleFrame(ManagedCamera cam, Mat frame, CameraRuntimeStatus status, SystemSettings cfg, CancellationToken workerCt)
    {
        var id = cam.Id;
        var threshold = cam.MotionThreshold ?? cfg.DefaultMotionThresholdPercent;

        FrameChangeResult change;
        try { change = _changeDetector.Evaluate(id, frame, threshold); }
        catch { frame.Dispose(); throw; }

        status.LastChangePercent = Math.Round(change.ChangePercent, 2);

        // Dang cho xac nhan "xe da roi" cua o nao do: phai xu ly tiep khung nay DU anh khong doi (vung da trong, anh tinh
        // nen khong bao gio vuot nguong chuyen dong) — neu khong thi o se mai o trang thai "Da do".
        // Tuong tu khi bien so cua xe vua do chua chot: can them vai lan doc (bo phieu) du anh tinh khong doi.
        bool forced = !change.ShouldProcess &&
                      (_parking.HasPendingVacancy(SlotKeysOf(cam)) || _parking.HasPendingPlateRead(SlotKeysOf(cam)));

        if (!change.ShouldProcess && !forced)
        {
            frame.Dispose();
            status.State = "Cho (anh chua thay doi)";
            return null;
        }

        var floor = cam.Floor;
        var cameraZone = cam.Zone;
        var zones = cam.Zones.ToList(); // ban chup: nguoi dung sua vung giua chung khong anh huong viec dang cho
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var coalesced = _scheduler.EnqueueBackground(
            key: id,
            label: $"camera {id[..Math.Min(6, id.Length)]}{(string.IsNullOrWhiteSpace(cam.IpAddress) ? "" : " (" + cam.IpAddress + ")")}",
            work: async queueCt =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    if (workerCt.IsCancellationRequested) return; // camera da bi xoa/dung trong luc xep hang
                    status.State = "Dang nhan dien";
                    await _processor.ProcessAsync(id, frame, zones, null, floor, cameraZone, queueCt);
                    // Chi chot khung nay lam anh tham chieu moi SAU KHI nhan dien xong khong loi
                    _changeDetector.CommitReference(id);
                    status.LastProcessedUtc = DateTime.UtcNow;
                    status.LastProcessMs = Math.Round(sw.Elapsed.TotalMilliseconds);
                    status.LastError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status.LastError = ex.Message;
                    _logger.LogError(ex, "Loi nhan dien camera {CameraId}", id);
                }
                finally
                {
                    frame.Dispose();
                    done.TrySetResult();
                }
            },
            discard: () =>
            {
                frame.Dispose();
                done.TrySetResult();
            });

        status.LastQueuedUtc = DateTime.UtcNow;
        status.State = coalesced ? "Dang xep hang (thay khung cu)" : "Dang xep hang AI";
        return done.Task;
    }

    /// <summary>Khoá các ô đỗ do camera này quản lý: "{tầng}-{zone}-{số thứ tự}" của từng vị trí nhận diện hợp lệ.</summary>
    private static List<string> SlotKeysOf(ManagedCamera cam) =>
        cam.Zones.Select(z => SlotKey.ForZone(cam.Floor, cam.Zone, z.SlotId)).Where(k => k is not null).Select(k => k!).Distinct().ToList();

    private Task NotifySlotsChangedAsync(CancellationToken ct) => _hub.Clients.All.SendAsync("SlotsChanged", ct);

    private TimeSpan Fail(
        string cameraId, CameraRuntimeStatus status, ref int failures, string reason,
        TimeSpan interval, SystemSettings cfg, bool log = true)
    {
        failures++;
        var factor = Math.Pow(2, Math.Min(failures - 1, 10));
        var delay = TimeSpan.FromSeconds(Math.Min(cfg.MaxRetryBackoffSeconds, Math.Max(1, interval.TotalSeconds) * factor));

        status.Failures = failures;
        status.State = "Loi - cho thu lai";
        status.LastError = reason;
        status.NextAttemptUtc = DateTime.UtcNow + delay;

        // Chi ghi log o cac lan 1, 2, 4, 8... de khong tran log khi camera chet lau
        if (log && (failures & (failures - 1)) == 0)
            _logger.LogWarning("Camera {CameraId}: {Reason}. Thu lai sau {Delay:0}s (lan loi thu {N}).",
                cameraId, reason, delay.TotalSeconds, failures);
        return delay;
    }
}
