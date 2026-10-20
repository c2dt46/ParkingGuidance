using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Camera;

public enum FrameReadStatus { Ok, Failed, Timeout }

public readonly record struct FrameReadResult(FrameReadStatus Status, Mat? Frame);

/// <summary>Kết quả mở nguồn video. <see cref="Busy"/> = chưa tới lượt mở vì thiết bị KHÁC đang chiếm cổng mở (không phải lỗi của camera này).</summary>
public enum CaptureOpenStatus { Ok, Unreachable, Timeout, Busy, Failed }

public readonly record struct CaptureOpenResult(CaptureOpenStatus Status, VideoCapture? Capture, string? Error);

/// <summary>
/// Mở / đọc camera CÓ GIỚI HẠN THỜI GIAN và CÁCH LY lỗi giữa các camera.
///
/// Vì sao cần: VideoCapture.Open/Read của OpenCV là lời gọi chặn (blocking). Với RTSP, khi camera mất mạng lời gọi mở có thể
/// treo hàng chục giây — và phần mở của backend FFmpeg trong OpenCV giữ MỘT KHOÁ TOÀN TIẾN TRÌNH, nên 1 camera hỏng đang mở
/// sẽ làm mọi camera khác (kể cả camera tốt đang kết nối lại) phải chờ theo → hết timeout → bị coi là lỗi.
///
/// Các biện pháp:
///  1) Dò nhanh TCP (≤ ~2s) tới host:port của nguồn mạng TRƯỚC khi gọi OpenCV → camera mất mạng bị loại ngay,
///     không bao giờ vào lời gọi chặn/khoá toàn cục của OpenCV.
///  2) Chỉ cho 1 lượt MỞ native tại 1 thời điểm (OpenGate) — giữ đúng đến khi lời gọi native thật sự kết thúc,
///     nhờ vậy không dồn đống luồng bị kẹt; ai không tới lượt kịp trả về <see cref="CaptureOpenStatus.Busy"/> (không tính là lỗi camera).
///  3) Lời gọi chặn chạy ở LUỒNG RIÊNG (không dùng ThreadPool) → camera treo không làm cạn ThreadPool, các timer/tác vụ khác không bị trễ.
///  4) Nguồn có lời gọi bị bỏ rơi (quá hạn mà chưa kết thúc) bị đánh dấu "kẹt" để không mở chồng thêm lần nữa cho tới khi lời gọi cũ trả về.
/// </summary>
public static class VideoCaptureHelper
{
    /// <summary>Thời gian tối đa dò TCP tới camera trước khi mở.</summary>
    private static readonly TimeSpan MaxProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly SemaphoreSlim OpenGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, DateTime> StuckSources = new(StringComparer.Ordinal);

    /// <summary>Đặt (nếu chưa có) tuỳ chọn FFmpeg để RTSP/HTTP tự ngắt socket khi mất kết nối, dùng TCP cho ổn định.
    /// Tuỳ chọn "timeout" (FFmpeg ≥ 5) và "stimeout" (FFmpeg cũ hơn) cùng được đặt cho chắc. Chỉ là lớp bảo vệ thêm —
    /// cơ chế chính là dò TCP + timeout bên dưới. Phải gọi trước lần mở VideoCapture đầu tiên của tiến trình.</summary>
    public static void EnsureFfmpegTimeout(TimeSpan timeout)
    {
        const string name = "OPENCV_FFMPEG_CAPTURE_OPTIONS";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return;
        var micros = (long)timeout.TotalMilliseconds * 1000;
        Environment.SetEnvironmentVariable(name, $"rtsp_transport;tcp|timeout;{micros}|stimeout;{micros}");
    }

    // ------------------------------------------------------------------------------------------------
    // Tiện ích nội bộ
    // ------------------------------------------------------------------------------------------------

    /// <summary>Chạy lời gọi chặn ở luồng riêng (nền) thay vì ThreadPool.</summary>
    private static Task<T> RunOnDedicatedThread<T>(Func<T> func, string name)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = name };
        thread.Start();
        return tcs.Task;
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        if (timeout < TimeSpan.FromMilliseconds(1)) timeout = TimeSpan.FromMilliseconds(1);
        using var cts = new CancellationTokenSource();
        var delay = Task.Delay(timeout, cts.Token);
        var winner = await Task.WhenAny(task, delay);
        if (winner == task)
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    /// <summary>Lấy host/cổng nếu nguồn là địa chỉ mạng (rtsp/rtsps/http/https/rtmp); file, chỉ số webcam... trả về false.</summary>
    private static bool TryGetEndpoint(string source, out string host, out int port)
    {
        host = "";
        port = 0;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;

        int defaultPort = uri.Scheme.ToLowerInvariant() switch
        {
            "rtsp" or "rtsps" => 554,
            "http" => 80,
            "https" => 443,
            "rtmp" => 1935,
            _ => -1
        };
        if (defaultPort < 0 || string.IsNullOrEmpty(uri.Host)) return false;

        host = uri.Host;
        port = uri.Port > 0 ? uri.Port : defaultPort;
        return true;
    }

    /// <summary>Dò nhanh kết nối TCP tới nguồn mạng. Trả về null nếu thông (hoặc nguồn không phải mạng), ngược lại là lý do.</summary>
    public static async Task<string?> ProbeAsync(string source, TimeSpan timeout, CancellationToken ct)
    {
        if (!TryGetEndpoint(source, out var host, out var port)) return null;

        if (timeout > MaxProbeTimeout) timeout = MaxProbeTimeout;
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return $"khong ket noi duoc toi {host}:{port} (qua {timeout.TotalSeconds:0.#}s - thiet bi mat mang?)";
        }
        catch (Exception ex)
        {
            return $"khong ket noi duoc toi {host}:{port}: {ex.Message}";
        }
    }

    /// <summary>Giải phóng capture ở luồng riêng: đóng kết nối RTSP của camera đã mất mạng có thể chờ gửi TEARDOWN tới hết timeout,
    /// không được để việc đó chặn vòng lặp của camera (hay luồng của ThreadPool).</summary>
    public static void DisposeInBackground(VideoCapture? capture)
    {
        if (capture is null) return;
        _ = Task.Factory.StartNew(() =>
        {
            try { capture.Dispose(); } catch { /* bo qua */ }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------------------------------------
    // Mở nguồn
    // ------------------------------------------------------------------------------------------------

    /// <summary>Mở nguồn (chỉ số webcam / đường dẫn file / URL RTSP) có giới hạn thời gian <paramref name="timeout"/> (tổng cả dò TCP, chờ lượt và mở).</summary>
    public static async Task<CaptureOpenResult> OpenAsync(string source, TimeSpan timeout, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();

        if (StuckSources.ContainsKey(source))
            return new CaptureOpenResult(CaptureOpenStatus.Failed, null,
                "lan mo/doc truoc cua nguon nay van chua ket thuc (camera khong phan hoi)");

        // 1) Dò TCP: camera mất mạng bị loại ngay, không đụng tới lời gọi chặn/khoá toàn cục của OpenCV
        var probeError = await ProbeAsync(source, timeout, ct);
        if (probeError is not null)
            return new CaptureOpenResult(CaptureOpenStatus.Unreachable, null, probeError);

        // 2) Chờ lượt mở (mỗi lúc chỉ 1 lượt mở native, giống khoá nội bộ của OpenCV nhưng không dồn đống luồng)
        var remaining = timeout - total.Elapsed;
        if (remaining < TimeSpan.FromMilliseconds(100)) remaining = TimeSpan.FromMilliseconds(100);
        if (!await OpenGate.WaitAsync(remaining, ct))
            return new CaptureOpenResult(CaptureOpenStatus.Busy, null,
                "dang cho luot mo ket noi (thiet bi khac dang mo)");

        // 3) Mở ở luồng riêng; cổng chỉ nhả khi lời gọi native THẬT SỰ kết thúc (kể cả khi người gọi đã bỏ chờ)
        Task<VideoCapture?> openTask;
        try
        {
            openTask = RunOnDedicatedThread<VideoCapture?>(() =>
            {
                try
                {
                    var capture = int.TryParse(source, out var idx) ? new VideoCapture(idx) : new VideoCapture(source);
                    if (capture.IsOpened()) return capture;
                    capture.Dispose();
                    return null;
                }
                finally { OpenGate.Release(); }
            }, "camera-open");
        }
        catch
        {
            OpenGate.Release(); // không tạo được luồng
            throw;
        }

        remaining = timeout - total.Elapsed;
        if (await CompletesWithinAsync(openTask, remaining))
        {
            try
            {
                var capture = await openTask;
                return capture is not null
                    ? new CaptureOpenResult(CaptureOpenStatus.Ok, capture, null)
                    : new CaptureOpenResult(CaptureOpenStatus.Failed, null, "khong mo duoc nguon video");
            }
            catch (Exception ex)
            {
                return new CaptureOpenResult(CaptureOpenStatus.Failed, null, "loi khi mo nguon video: " + ex.Message);
            }
        }

        // Quá hạn: luồng mở vẫn chạy ngầm; đánh dấu nguồn "kẹt", khi nó xong (nếu mở được) thì giải phóng để không rò rỉ.
        StuckSources[source] = DateTime.UtcNow;
        _ = openTask.ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose();
            else _ = t.Exception; // đánh dấu đã quan sát lỗi
            StuckSources.TryRemove(source, out _);
        }, TaskScheduler.Default);

        return new CaptureOpenResult(CaptureOpenStatus.Timeout, null,
            $"mo nguon qua {timeout.TotalSeconds:0}s khong xong");
    }

    // ------------------------------------------------------------------------------------------------
    // Đọc khung hình
    // ------------------------------------------------------------------------------------------------
    private const int DrainMaxFrames = 200;
    private const int DrainLiveGrabMs = 20;
    private const int DrainBudgetMs = 2000;

    /// <summary>Đọc 1 khung hình. Nếu <see cref="FrameReadStatus.Timeout"/>: capture đang bị luồng đọc chiếm,
    /// người gọi PHẢI bỏ tham chiếu (helper sẽ tự giải phóng capture khi lời gọi chặn kết thúc).
    /// <paramref name="latest"/> = true (luồng RTSP/camera chạy liên tục): bỏ qua các khung đã bị dồn trong bộ đệm
    /// để lấy khung MỚI NHẤT — nếu không, mỗi lần đọc cách nhau vài giây sẽ nhận khung ngày càng cũ (trễ tích luỹ).
    /// Không dùng cho file video (đọc nhanh hết mức, sẽ tua nhanh cả file).
    /// <paramref name="source"/>: nếu quá hạn, nguồn này được đánh dấu "kẹt" tới khi lời gọi cũ kết thúc (tránh mở chồng).</summary>
    public static async Task<FrameReadResult> ReadAsync(
        VideoCapture capture, TimeSpan timeout, bool latest = false, string? source = null)
    {
        var readTask = RunOnDedicatedThread<Mat?>(() =>
        {
            var frame = new Mat();
            bool ok;
            if (latest)
            {
                // Khung da dong trong bo dem tra ve rat nhanh; khi bat kip luong truc tiep thi Grab phai cho khung
                // ke tiep (>= vai chuc ms) -> dung lai o do va giai ma khung vua lay.
                var total = Stopwatch.StartNew();
                var sw = new Stopwatch();
                bool got = false;
                for (int i = 0; i < DrainMaxFrames; i++)
                {
                    sw.Restart();
                    if (!capture.Grab()) break;
                    got = true;
                    if (i > 0 && sw.ElapsedMilliseconds >= DrainLiveGrabMs) break;
                    if (total.ElapsedMilliseconds >= DrainBudgetMs) break;
                }
                ok = got && capture.Retrieve(frame, 0);
            }
            else
            {
                ok = capture.Read(frame);
            }

            if (ok && !frame.Empty()) return frame;
            frame.Dispose();
            return null;
        }, "camera-read");

        if (await CompletesWithinAsync(readTask, timeout))
        {
            Mat? frame;
            try { frame = await readTask; }
            catch { return new FrameReadResult(FrameReadStatus.Failed, null); }

            return frame is null
                ? new FrameReadResult(FrameReadStatus.Failed, null)
                : new FrameReadResult(FrameReadStatus.Ok, frame);
        }

        if (source is not null) StuckSources[source] = DateTime.UtcNow;
        _ = readTask.ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose();
            else _ = t.Exception;
            capture.Dispose();
            if (source is not null) StuckSources.TryRemove(source, out _);
        }, TaskScheduler.Default);
        return new FrameReadResult(FrameReadStatus.Timeout, null);
    }

    /// <summary>Mở → đọc đúng 1 khung → đóng, tất cả có timeout. Dùng cho "Lấy ảnh ngay"/"Nhận diện ngay".</summary>
    public static async Task<(Mat? Frame, string? Error)> GrabOnceAsync(
        string source, TimeSpan openTimeout, TimeSpan readTimeout)
    {
        var open = await OpenAsync(source, openTimeout);
        if (open.Status != CaptureOpenStatus.Ok || open.Capture is null)
            return (null, $"Khong mo duoc nguon video ({open.Error}): {source}");

        var capture = open.Capture;
        var result = await ReadAsync(capture, readTimeout, source: source);
        switch (result.Status)
        {
            case FrameReadStatus.Ok:
                capture.Dispose();
                return (result.Frame, null);
            case FrameReadStatus.Timeout: // capture do helper tự giải phóng
                return (null, $"Khong lay duoc khung hinh trong {readTimeout.TotalSeconds:0}s (camera khong phan hoi).");
            default:
                capture.Dispose();
                return (null, "Khong doc duoc khung hinh tu nguon video.");
        }
    }
}
