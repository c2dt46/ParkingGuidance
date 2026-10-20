using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Dò vùng biển số bằng YOLOv3-tiny (Darknet .cfg + .weights) của repo buiquangmanhhp1999/License-Plate-Recognition,
/// chạy trực tiếp bằng OpenCV DNN (không cần chuyển sang ONNX). Nếu ảnh lớn mà biển nhỏ (không thấy khung nào) thì
/// chia ảnh thành các ô chồng lấn (2x2 rồi 3x3) và dò lại từng ô.
/// Chỉ bật khi có đủ file cfg + weights (xem Anpr:PlateCfgPath / Anpr:PlateWeightsPath).
/// </summary>
public sealed class DarknetPlateDetector : IDisposable
{
    private readonly Net _net;
    private readonly string[] _outNames;

    private DarknetPlateDetector(Net net)
    {
        _net = net;
        _outNames = net.GetUnconnectedOutLayersNames();
    }

    public static DarknetPlateDetector? TryCreate(string cfgPath, string weightsPath, ILogger logger)
    {
        try
        {
            var net = CvDnn.ReadNetFromDarknet(cfgPath, weightsPath);
            if (net.Empty()) { logger.LogWarning("Khong nap duoc model Darknet {Cfg}", cfgPath); return null; }
            logger.LogInformation("Da nap model do bien so YOLOv3-tiny (Darknet): {Weights}", weightsPath);
            return new DarknetPlateDetector(net);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Khong khoi tao duoc DarknetPlateDetector — dung phuong an du phong.");
            return null;
        }
    }

    public List<(Rect Rect, float Score)> Detect(Mat image, float confThreshold = 0.5f, int maxResults = 3)
    {
        var result = DetectOnce(image, confThreshold);
        if (result.Count == 0)
        {
            foreach (int n in new[] { 2, 3 })
            {
                result = DetectTiles(image, n, confThreshold);
                if (result.Count > 0) break;
            }
        }
        return result.OrderByDescending(r => r.Score).Take(maxResults).ToList();
    }

    /// <summary>Dò 1 lượt trên cả ảnh (không chia ô) — rẻ, dùng để BỔ SUNG khung khi bộ dò khác đã có kết quả.</summary>
    public List<(Rect Rect, float Score)> DetectQuick(Mat image, float confThreshold = 0.5f, int maxResults = 3)
        => DetectOnce(image, confThreshold).OrderByDescending(r => r.Score).Take(maxResults).ToList();

    /// <summary>
    /// Lần dò "nới lỏng" — chỉ gọi khi lần dò thường (ngưỡng 0.5) không ra biển đọc được. Biển bị mờ/nghiêng/xa/ngược sáng thường chỉ đạt điểm 0.15–0.5 nên bị bỏ sót.
    /// Chạy lần lượt: (1) ngưỡng 0.3 trên cả ảnh + ô 2x2 + 3x3; (2) ảnh đã cân bằng sáng CLAHE (ảnh tối/loá) ngưỡng 0.3; (3) nếu vẫn chưa thấy: ngưỡng 0.15.
    /// Ứng viên sai không sao: bước đọc chữ + kiểm tra định dạng biển VN sẽ loại. Kết quả sắp theo điểm giảm dần.
    /// </summary>
    public List<(Rect Rect, float Score)> DetectRelaxed(Mat image, int maxResults = 4)
    {
        var all = new List<(Rect Rect, float Score)>();
        if (image.Empty()) return all;

        all.AddRange(DetectOnce(image, 0.3f));
        foreach (int n in new[] { 2, 3 }) all.AddRange(DetectTiles(image, n, 0.3f));

        using (var enhanced = EnhanceContrast(image))
        {
            all.AddRange(DetectOnce(enhanced, 0.3f));
            all.AddRange(DetectTiles(enhanced, 2, 0.3f));
        }

        if (all.Count == 0)
        {
            all.AddRange(DetectOnce(image, 0.15f));
            all.AddRange(DetectTiles(image, 2, 0.15f));
        }
        return Nms(all, 0.4f).OrderByDescending(r => r.Score).Take(maxResults).ToList();
    }

    /// <summary>Cân bằng sáng kênh L (CLAHE) — giúp ảnh tối/thiếu sáng/ngược sáng.</summary>
    private static Mat EnhanceContrast(Mat bgr)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        var ch = Cv2.Split(lab);
        try
        {
            using var clahe = Cv2.CreateCLAHE(3.0, new Size(8, 8));
            using var l2 = new Mat();
            clahe.Apply(ch[0], l2);
            l2.CopyTo(ch[0]);
            Cv2.Merge(ch, lab);
        }
        finally
        {
            foreach (var m in ch) m.Dispose();
        }
        var result = new Mat();
        Cv2.CvtColor(lab, result, ColorConversionCodes.Lab2BGR);
        return result;
    }

    private List<(Rect Rect, float Score)> DetectTiles(Mat image, int n, float conf)
    {
        int W = image.Width, H = image.Height;
        double k = n - (n - 1) * 0.3;                 // o chong lan 30%
        int tw = (int)(W / k), th = (int)(H / k);
        double sx = n > 1 ? (W - tw) / (double)(n - 1) : 0, sy = n > 1 ? (H - th) / (double)(n - 1) : 0;

        var all = new List<(Rect Rect, float Score)>();
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                int x0 = (int)(j * sx), y0 = (int)(i * sy);
                var roi = new Rect(x0, y0, Math.Min(tw, W - x0), Math.Min(th, H - y0));
                if (roi.Width < 32 || roi.Height < 32) continue;
                using var tile = new Mat(image, roi);
                foreach (var (r, s) in DetectOnce(tile, conf))
                    all.Add((new Rect(r.X + x0, r.Y + y0, r.Width, r.Height), s));
            }
        }
        return Nms(all, 0.4f);
    }

    private List<(Rect Rect, float Score)> DetectOnce(Mat image, float confThreshold)
    {
        var boxes = new List<(Rect Rect, float Score)>();
        if (image.Empty()) return boxes;

        // Giong repo: scale 1/255, 416x416, doi BGR->RGB, khong crop
        using var blob = CvDnn.BlobFromImage(image, 1.0 / 255.0, new Size(416, 416), new Scalar(0, 0, 0), true, false);
        _net.SetInput(blob);
        var outs = _outNames.Select(_ => new Mat()).ToArray();
        try
        {
            _net.Forward(outs, _outNames);
            int W = image.Width, H = image.Height;
            foreach (var o in outs)
            {
                if (o.Empty() || o.Cols < 6) continue;
                using var cont = o.IsContinuous() ? o.Reshape(1, o.Rows) : o.Clone();
                int rows = cont.Rows, cols = cont.Cols;
                var data = new float[rows * cols];
                Marshal.Copy(cont.Data, data, 0, data.Length);
                for (int r = 0; r < rows; r++)
                {
                    int b = r * cols;
                    float best = 0f;
                    for (int c = 5; c < cols; c++) best = Math.Max(best, data[b + c]);
                    if (best <= confThreshold) continue;

                    double cx = data[b] * W, cy = data[b + 1] * H, w = data[b + 2] * W, h = data[b + 3] * H;
                    int x1 = (int)Math.Round(cx - w / 2), y1 = (int)Math.Round(cy - h / 2);
                    var rect = Clamp(new Rect(x1, y1, (int)w, (int)h), W, H);
                    if (rect.Width < 8 || rect.Height < 8) continue;
                    boxes.Add((rect, best));
                }
            }
        }
        finally
        {
            foreach (var m in outs) m.Dispose();
        }
        return Nms(boxes, 0.4f);
    }

    private static Rect Clamp(Rect r, int W, int H)
    {
        int x1 = Math.Max(0, r.X), y1 = Math.Max(0, r.Y);
        int x2 = Math.Min(W, r.X + r.Width), y2 = Math.Min(H, r.Y + r.Height);
        return new Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    private static List<(Rect Rect, float Score)> Nms(List<(Rect Rect, float Score)> boxes, float iouThr)
    {
        var keep = new List<(Rect Rect, float Score)>();
        foreach (var b in boxes.OrderByDescending(x => x.Score))
        {
            bool overlap = false;
            foreach (var k in keep)
            {
                int ix = Math.Max(b.Rect.X, k.Rect.X), iy = Math.Max(b.Rect.Y, k.Rect.Y);
                int ax = Math.Min(b.Rect.Right, k.Rect.Right), ay = Math.Min(b.Rect.Bottom, k.Rect.Bottom);
                double inter = Math.Max(0, ax - ix) * (double)Math.Max(0, ay - iy);
                double uni = b.Rect.Width * (double)b.Rect.Height + k.Rect.Width * (double)k.Rect.Height - inter;
                if (uni > 0 && inter / uni > iouThr) { overlap = true; break; }
            }
            if (!overlap) keep.Add(b);
        }
        return keep;
    }

    public void Dispose() => _net.Dispose();
}
