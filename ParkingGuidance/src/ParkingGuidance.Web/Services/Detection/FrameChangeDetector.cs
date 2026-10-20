using System.Collections.Concurrent;
using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Detection;

/// <summary>
/// Tối ưu tải xử lý khi có NHIỀU camera cùng chạy nền: thay vì chạy YOLO+OCR (tốn CPU/GPU) cho
/// MỌI khung hình của MỌI camera, chỉ chạy khi ảnh thực sự thay đổi đáng kể so với lần xử lý gần
/// nhất — bãi xe phần lớn thời gian gần như tĩnh (không có xe ra/vào), nên phần lớn khung hình có
/// thể bỏ qua an toàn.
///
/// Cách đo "thay đổi": thu nhỏ ảnh về 48x48 pixel mức xám (rẻ, đủ để bắt thay đổi lớn như xe ra/
/// vào, bỏ qua rung nhiễu nhỏ/nén JPEG), tính sai khác tuyệt đối trung bình từng pixel so với
/// khung hình trước, quy về thang % (0-100% so với sai khác tối đa có thể 0↔255).
/// </summary>
public class FrameChangeDetector : IFrameChangeDetector
{
    private const int SampleSize = 48;
    private readonly ConcurrentDictionary<string, Mat> _lastFrames = new();
    // Khung dang cho "chot" lam anh tham chieu (da vuot nguong, cho nhan dien xong). Moi key 1 khung.
    private readonly ConcurrentDictionary<string, Mat> _pending = new();
    private readonly object _lock = new();

    public bool ShouldProcess(string key, Mat frame, double thresholdPercent) =>
        Evaluate(key, frame, thresholdPercent).ShouldProcess;

    public FrameChangeResult Evaluate(string key, Mat frame, double thresholdPercent)
    {
        if (frame.Empty()) return new FrameChangeResult(false, 0, false);
        if (thresholdPercent <= 0) return new FrameChangeResult(true, 0, false); // nguong 0 = luon chay, khong toi uu

        using var small = new Mat();
        Cv2.Resize(frame, small, new Size(SampleSize, SampleSize), interpolation: InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

        lock (_lock)
        {
            if (!_lastFrames.TryGetValue(key, out var last))
            {
                SetPending(key, gray); // chua co tham chieu: chi chot sau khi nhan dien thanh cong
                return new FrameChangeResult(true, 0, true); // lan dau cho key nay: chua co gi de so sanh, cu chay
            }

            using var diff = new Mat();
            Cv2.Absdiff(gray, last, diff);
            double meanDiff = Cv2.Mean(diff).Val0; // 0..255
            double changePercent = meanDiff / 255.0 * 100.0;

            bool shouldProcess = changePercent >= thresholdPercent;
            if (shouldProcess)
                SetPending(key, gray); // KHONG doi tham chieu ngay — doi o CommitReference sau khi nhan dien xong
            return new FrameChangeResult(shouldProcess, changePercent, false);
        }
    }

    private void SetPending(string key, Mat gray)
    {
        if (_pending.TryGetValue(key, out var old)) old.Dispose();
        _pending[key] = gray.Clone();
    }

    public void SetReference(string key, Mat frame)
    {
        if (frame.Empty()) return;

        using var small = new Mat();
        Cv2.Resize(frame, small, new Size(SampleSize, SampleSize), interpolation: InterpolationFlags.Area);
        var gray = new Mat();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

        lock (_lock)
        {
            if (_pending.TryRemove(key, out var pending)) pending.Dispose(); // bo khung dang cho chot (neu co)
            if (_lastFrames.TryGetValue(key, out var last)) last.Dispose();
            _lastFrames[key] = gray;
        }
    }

    public void CommitReference(string key)
    {
        lock (_lock)
        {
            if (!_pending.TryRemove(key, out var pending)) return;
            if (_lastFrames.TryGetValue(key, out var last)) last.Dispose();
            _lastFrames[key] = pending;
        }
    }
}
