using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// (TUỲ CHỌN) Dò vị trí biển số bằng model YOLO ONNX huấn luyện riêng cho biển số (1 lớp "license plate"), chạy trên ẢNH XE
/// đã cắt. Chính xác và bền hơn nhiều so với <see cref="PlateLocator"/> (xử lý ảnh cố định) khi camera nghiêng/xa/ngược sáng.
/// Chỉ bật khi tìm thấy file model (xem Anpr:DetectorModelPath trong appsettings.json); không có thì hệ thống tự dùng PlateLocator.
/// Hỗ trợ 2 dạng đầu ra phổ biến: YOLOv8 [1, 4+số_lớp, N] (cx,cy,w,h,điểm...) và dạng đã NMS [1, N, 6] (x1,y1,x2,y2,điểm,lớp).
/// </summary>
public sealed class OnnxPlateDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inW, _inH;

    private OnnxPlateDetector(InferenceSession session)
    {
        _session = session;
        _inputName = session.InputMetadata.Keys.First();
        var dims = session.InputMetadata[_inputName].Dimensions; // [1, 3, H, W]; -1 = kich thuoc dong
        _inH = dims.Length >= 4 && dims[2] > 0 ? dims[2] : 640;
        _inW = dims.Length >= 4 && dims[3] > 0 ? dims[3] : 640;
    }

    public static OnnxPlateDetector? TryCreate(string path, ILogger logger)
    {
        try
        {
            var d = new OnnxPlateDetector(new InferenceSession(path));
            logger.LogInformation("Da tai model do bien so (ONNX): {Path} (dau vao {W}x{H})", path, d._inW, d._inH);
            return d;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tim thay model do bien so '{Path}' nhung khong nap duoc: se dung PlateLocator (xu ly anh).", path);
            return null;
        }
    }

    /// <summary>Trả về các khung biển số (toạ độ theo ẢNH XE ĐÃ CẮT, đã nới nhẹ viền), điểm cao trước.</summary>
    public List<(Rect Rect, float Score)> Detect(Mat vehicle, float confThreshold = 0.35f, int maxResults = 3, float expand = 0.06f)
    {
        var result = new List<(Rect Rect, float Score)>();
        if (vehicle.Empty()) return result;

        float scale = Math.Min((float)_inW / vehicle.Width, (float)_inH / vehicle.Height);
        int newW = Math.Max(1, (int)(vehicle.Width * scale));
        int newH = Math.Max(1, (int)(vehicle.Height * scale));
        float padX = (_inW - newW) / 2f, padY = (_inH - newH) / 2f;

        using var resized = new Mat();
        Cv2.Resize(vehicle, resized, new Size(newW, newH));
        using var canvas = new Mat(_inH, _inW, MatType.CV_8UC3, new Scalar(114, 114, 114));
        using (var roi = new Mat(canvas, new Rect((int)padX, (int)padY, newW, newH)))
            resized.CopyTo(roi);

        var data = new float[3 * _inH * _inW];
        int plane = _inH * _inW;
        for (int y = 0; y < _inH; y++)
        {
            for (int x = 0; x < _inW; x++)
            {
                var px = canvas.At<Vec3b>(y, x); // BGR
                int idx = y * _inW + x;
                data[idx] = px.Item2 / 255f;
                data[plane + idx] = px.Item1 / 255f;
                data[2 * plane + idx] = px.Item0 / 255f;
            }
        }

        var tensor = new DenseTensor<float>(data, new[] { 1, 3, _inH, _inW });
        using var outputs = _session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var output = outputs.First().AsTensor<float>();
        var dims = output.Dimensions.ToArray();

        var cands = new List<(float X1, float Y1, float X2, float Y2, float Score)>();

        if (dims.Length == 3 && dims[1] >= 5 && dims[1] < dims[2])
        {
            // YOLOv8: [1, 4 + so_lop, N]
            int c = dims[1], n = dims[2];
            float maxW = 0f, maxH = 0f;
            for (int i = 0; i < n; i++)
            {
                maxW = Math.Max(maxW, output[0, 2, i]);
                maxH = Math.Max(maxH, output[0, 3, i]);
            }
            bool normalized = maxW <= 2f && maxH <= 2f; // mot so file ONNX tra toa do chuan hoa 0-1
            float sx = normalized ? _inW : 1f, sy = normalized ? _inH : 1f;

            for (int i = 0; i < n; i++)
            {
                float score = 0f;
                for (int k = 4; k < c; k++) score = Math.Max(score, output[0, k, i]);
                if (score < confThreshold) continue;

                float cx = output[0, 0, i] * sx, cy = output[0, 1, i] * sy;
                float w = output[0, 2, i] * sx, h = output[0, 3, i] * sy;
                cands.Add(((cx - w / 2 - padX) / scale, (cy - h / 2 - padY) / scale,
                           (cx + w / 2 - padX) / scale, (cy + h / 2 - padY) / scale, score));
            }
        }
        else if (dims.Length == 3 && dims[2] >= 6)
        {
            // Da NMS san: [1, N, 6] = x1, y1, x2, y2, diem, lop (toa do theo pixel dau vao)
            for (int i = 0; i < dims[1]; i++)
            {
                float score = output[0, i, 4];
                if (score < confThreshold) continue;
                cands.Add(((output[0, i, 0] - padX) / scale, (output[0, i, 1] - padY) / scale,
                           (output[0, i, 2] - padX) / scale, (output[0, i, 3] - padY) / scale, score));
            }
        }
        else
        {
            return result; // dang dau ra khong ho tro
        }

        // NMS
        var sorted = cands.OrderByDescending(c => c.Score).ToList();
        var kept = new List<(float X1, float Y1, float X2, float Y2, float Score)>();
        while (sorted.Count > 0 && kept.Count < maxResults)
        {
            var best = sorted[0];
            kept.Add(best);
            sorted.RemoveAt(0);
            sorted.RemoveAll(c => IoU(best, c) > 0.45f);
        }

        foreach (var k in kept)
        {
            // Noi nhe vien (mac dinh 6% moi ben) de khong cat mat net chu sat mep, roi gioi han trong anh xe.
            // expand = 0 khi can khung om sat (goi y khung cho tab Train bien so).
            float bw = k.X2 - k.X1, bh = k.Y2 - k.Y1;
            int x1 = (int)Math.Max(0, k.X1 - bw * expand), y1 = (int)Math.Max(0, k.Y1 - bh * expand);
            int x2 = (int)Math.Min(vehicle.Width, k.X2 + bw * expand), y2 = (int)Math.Min(vehicle.Height, k.Y2 + bh * expand);
            if (x2 - x1 < 24 || y2 - y1 < 8) continue;
            result.Add((new Rect(x1, y1, x2 - x1, y2 - y1), k.Score));
        }
        return result;
    }

    private static float IoU((float X1, float Y1, float X2, float Y2, float Score) a, (float X1, float Y1, float X2, float Y2, float Score) b)
    {
        float x1 = Math.Max(a.X1, b.X1), y1 = Math.Max(a.Y1, b.Y1);
        float x2 = Math.Min(a.X2, b.X2), y2 = Math.Min(a.Y2, b.Y2);
        float inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Kích thước ảnh vào của model, dùng để hiển thị (vd "640x640").</summary>
    public string InputSizeText => $"{_inW}x{_inH}";

    public void Dispose() => _session.Dispose();
}
