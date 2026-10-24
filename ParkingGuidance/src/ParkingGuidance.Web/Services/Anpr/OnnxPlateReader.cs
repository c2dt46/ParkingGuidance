using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// (TUỲ CHỌN) Đọc chữ trên ảnh biển số bằng model OCR ONNX kiểu CRNN + CTC (đọc nguyên dòng, không tách ký tự) — chính xác hơn
/// Tesseract nhiều với biển số. Biển 2 dòng được tách đôi (trên/dưới), đọc từng dòng rồi ghép.
/// Chỉ bật khi có file model (Anpr:OcrModelPath); nếu không, hệ thống dùng Tesseract như trước.
///
/// Cấu hình trong appsettings.json (mục "Anpr"):
///   OcrCharset      chuỗi ký tự model nhận ra (mặc định 36 ký tự "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"; model cũ 31 ký tự vẫn tự nhận ra) hoặc OcrCharsetFile (mỗi dòng 1 ký tự)
///   OcrBlankLast    false (mặc định) = chỉ số 0 là "blank" CTC và ký tự thứ i ứng với chỉ số i+1; true = blank là chỉ số cuối
///   OcrNormalize    "signed" (mặc định, (x/255-0.5)/0.5) hoặc "unit" (x/255)
/// Kích thước/số kênh ảnh vào đọc từ chính model ([1, 1|3, H, W]); chiều rộng động thì tự tính theo tỉ lệ ảnh.
/// Đầu ra [1, T, C] hoặc [1, C, T] (C = số ký tự + 1), tự nhận biết là xác suất hay logit.
/// </summary>
public sealed class OnnxPlateReader : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _channels, _inH, _inW; // _inW = 0: dong theo anh
    private readonly string _charset;
    private readonly bool _blankLast;
    private readonly bool _signedNorm;

    private OnnxPlateReader(InferenceSession session, string charset, bool blankLast, bool signedNorm)
    {
        _session = session;
        _charset = charset;
        _blankLast = blankLast;
        _signedNorm = signedNorm;
        _inputName = session.InputMetadata.Keys.First();
        var dims = session.InputMetadata[_inputName].Dimensions; // [1, C, H, W]
        _channels = dims.Length >= 4 && dims[1] == 1 ? 1 : 3;
        _inH = dims.Length >= 4 && dims[2] > 0 ? dims[2] : 48;
        _inW = dims.Length >= 4 && dims[3] > 0 ? dims[3] : 0;
    }

    public static OnnxPlateReader? TryCreate(string path, IConfiguration config, ILogger logger)
    {
        try
        {
            var charset = config["Anpr:OcrCharset"] ?? "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var charsetFile = config["Anpr:OcrCharsetFile"];
            if (!string.IsNullOrWhiteSpace(charsetFile) && File.Exists(charsetFile))
                charset = string.Concat(File.ReadAllLines(charsetFile).Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l[0]));

            bool blankLast = config.GetValue("Anpr:OcrBlankLast", false);
            bool signed = !string.Equals(config["Anpr:OcrNormalize"], "unit", StringComparison.OrdinalIgnoreCase);

            var session = new InferenceSession(path);
            charset = charset.ToUpperInvariant();

            // Tu nhan bang ky tu theo so lop dau ra cua model: 37 lop = 36 ky tu (A-Z, 0-9), 32 lop = 31 ky tu cu (khong co I J O Q W).
            // Nho vay doi model (cu <-> moi) khong bat buoc phai sua OcrCharset trong appsettings.json.
            const string full = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ", legacy = "0123456789ABCDEFGHKLMNPRSTUVXYZ";
            int classes = 0;
            var outMeta = session.OutputMetadata.Values.FirstOrDefault();
            if (outMeta is not null && outMeta.Dimensions.Length > 0) classes = outMeta.Dimensions[^1];
            if (classes > 0 && classes != charset.Length + 1)
            {
                var alt = new[] { full, legacy }.FirstOrDefault(c => c.Length + 1 == classes);
                if (alt is not null)
                {
                    logger.LogInformation("Model OCR co {C} lop: dung bang {N} ky tu thay cho OcrCharset ({M} ky tu).", classes, alt.Length, charset.Length);
                    charset = alt;
                }
            }

            var r = new OnnxPlateReader(session, charset, blankLast, signed);
            logger.LogInformation("Da tai model OCR bien so (ONNX): {Path} (anh vao {C}x{H}x{W}, {N} ky tu)", path, r._channels, r._inH, r._inW, charset.Length);
            return r;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tim thay model OCR bien so '{Path}' nhung khong nap duoc: se dung Tesseract.", path);
            return null;
        }
    }

    /// <summary>Đọc 1 ảnh biển số (BGR). Trả về (chuỗi thô, độ tin cậy 0-1, độ dài từng dòng nếu biển 2 dòng).</summary>
    public (string? Text, float Confidence, IReadOnlyList<int>? LineLengths) Read(Mat plateBgr)
    {
        if (plateBgr.Empty() || plateBgr.Width < 8 || plateBgr.Height < 8) return (null, 0f, null);

        // Ti le rong/cao < ~2.4 => bien vuong 2 dong: tach doi, doc tung dong
        if (plateBgr.Width / (double)plateBgr.Height < 2.4)
        {
            int half = plateBgr.Height / 2;
            int overlap = Math.Max(1, plateBgr.Height / 20);
            using var top = new Mat(plateBgr, new Rect(0, 0, plateBgr.Width, Math.Min(plateBgr.Height, half + overlap)));
            int y2 = Math.Max(0, half - overlap);
            using var bottom = new Mat(plateBgr, new Rect(0, y2, plateBgr.Width, plateBgr.Height - y2));

            var (t1, c1) = ReadLine(top);
            var (t2, c2) = ReadLine(bottom);
            if (string.IsNullOrEmpty(t1) || string.IsNullOrEmpty(t2)) return (null, 0f, null);
            return (t1 + t2, Math.Min(c1, c2), new[] { t1.Length, t2.Length });
        }

        var (text, conf) = ReadLine(plateBgr);
        return string.IsNullOrEmpty(text) ? (null, 0f, null) : (text, conf, null);
    }

    private (string Text, float Confidence) ReadLine(Mat img)
    {
        int h = _inH;
        int w = _inW > 0 ? _inW : Math.Clamp((int)Math.Round(h * img.Width / (double)img.Height), 16, 1024);
        int newW = Math.Max(1, Math.Min(w, (int)Math.Round(h * img.Width / (double)img.Height)));

        using var resized = new Mat();
        Cv2.Resize(img, resized, new Size(newW, h));

        var data = new float[_channels * h * w]; // vung dem ben phai de nguyen 0
        int plane = h * w;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < newW; x++)
            {
                var px = resized.At<Vec3b>(y, x); // BGR
                int idx = y * w + x;
                if (_channels == 1)
                {
                    data[idx] = Norm((px.Item0 * 0.114f + px.Item1 * 0.587f + px.Item2 * 0.299f) / 255f);
                }
                else
                {
                    data[idx] = Norm(px.Item2 / 255f);
                    data[plane + idx] = Norm(px.Item1 / 255f);
                    data[2 * plane + idx] = Norm(px.Item0 / 255f);
                }
            }
        }

        var tensor = new DenseTensor<float>(data, new[] { 1, _channels, h, w });
        using var outputs = _session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var output = outputs.First().AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        if (dims.Length != 3) return ("", 0f);

        int classes = _charset.Length + 1;
        bool timeFirst;
        if (dims[2] == classes) timeFirst = true;       // [1, T, C]
        else if (dims[1] == classes) timeFirst = false; // [1, C, T]
        else return ("", 0f);                           // so lop khong khop bo ky tu da cau hinh

        int steps = timeFirst ? dims[1] : dims[2];
        int blank = _blankLast ? classes - 1 : 0;
        var row = new float[classes];
        var sb = new System.Text.StringBuilder();
        float confSum = 0f;
        int confN = 0;
        int prev = -1;

        for (int t = 0; t < steps; t++)
        {
            for (int k = 0; k < classes; k++) row[k] = timeFirst ? output[0, t, k] : output[0, k, t];

            // Tu nhan biet: da la xac suat (tong ~1, khong am) thi giu nguyen, nguoc lai tinh softmax
            float sum = 0f, min = float.MaxValue, max = float.MinValue;
            foreach (var v in row) { sum += v; min = Math.Min(min, v); max = Math.Max(max, v); }
            if (!(min >= 0f && Math.Abs(sum - 1f) < 0.05f))
            {
                float expSum = 0f;
                for (int k = 0; k < classes; k++) { row[k] = MathF.Exp(row[k] - max); expSum += row[k]; }
                for (int k = 0; k < classes; k++) row[k] /= expSum;
            }

            int arg = 0;
            for (int k = 1; k < classes; k++) if (row[k] > row[arg]) arg = k;

            if (arg != blank && arg != prev)
            {
                int ci = _blankLast ? arg : arg - 1;
                if (ci >= 0 && ci < _charset.Length)
                {
                    sb.Append(_charset[ci]);
                    confSum += row[arg];
                    confN++;
                }
            }
            prev = arg;
        }

        return (sb.ToString(), confN == 0 ? 0f : confSum / confN);
    }

    private float Norm(float v) => _signedNorm ? (v - 0.5f) / 0.5f : v;

    public void Dispose() => _session.Dispose();
}
