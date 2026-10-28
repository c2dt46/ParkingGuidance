using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Đọc ký tự biển số bằng model DÒ ĐỐI TƯỢNG YOLO11 (31 lớp = 31 ký tự biển Việt Nam), huấn luyện bằng <c>tools/train_chars.py</c>.
/// Khác <see cref="CnnPlateReader"/> (nhị phân hoá → tách thành phần liên thông → phân loại từng ký tự): ở đây model nhìn thẳng ảnh biển và
/// trả về vị trí + loại của từng ký tự, nên KHÔNG phụ thuộc bước nhị phân hoá/tách ký tự — vốn là chỗ sai nhiều nhất khi biển nghiêng, mờ,
/// chữ dính nhau hoặc dính khung viền. Kết quả được xếp thành 1/2 dòng, chuẩn hoá bằng <see cref="VietnamPlateFormatter"/> và đưa vào cùng hệ bỏ phiếu.
/// Thứ tự lớp PHẢI trùng bảng ký tự bên dưới (train_chars.py dùng đúng bảng này).
/// </summary>
public sealed class YoloCharReader : IDisposable
{
    /// <summary>Bảng ký tự cũ (31 lớp) — thiếu I, J, O, Q, W nên KHÔNG đọc được sêri "QT" (tổ chức quốc tế) và các ký hiệu quân đội có chữ Q.</summary>
    private const string LegacyAlphabet = "ABCDEFGHKLMNPRSTUVXYZ0123456789";
    /// <summary>Bảng ký tự của model đang nạp: đọc từ file <c>&lt;tên model&gt;.charset.txt</c> (mỗi dòng 1 ký tự, đúng thứ tự lớp khi train) nếu có,
    /// ngược lại dùng bảng cũ. Train lại với bảng đầy đủ (vd 36 ký tự) thì chỉ cần đặt file charset cạnh model — không phải sửa code.</summary>
    private readonly string _alphabet;
    /// <summary>Phiếu của bộ đọc này được nhân hệ số này (đáng tin hơn đọc cổ điển khi đã train đủ dữ liệu).</summary>
    private const double TrustFactor = 2.0;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inW, _inH;

    /// <summary>Ngưỡng tin cậy của TỪNG khung ký tự. Đặt cố định thấp; việc lọc lần đọc kém dùng <see cref="MinCharConfidence"/>.</summary>
    public volatile float BoxConfidence = 0.25f;

    /// <summary>Độ tin cậy TRUNG BÌNH tối thiểu của các ký tự trong 1 lần đọc; thấp hơn thì bỏ. Đặt từ cài đặt chung (cùng ô với CNN).</summary>
    public volatile float MinCharConfidence;

    private sealed class Det
    {
        public float X1, Y1, X2, Y2, Score;
        public int Cls;
        public float Cx => (X1 + X2) / 2f;
        public float Cy => (Y1 + Y2) / 2f;
        public float W => X2 - X1;
        public float H => Y2 - Y1;
    }

    private YoloCharReader(InferenceSession session, int inW, int inH, string alphabet)
    {
        _session = session;
        _alphabet = alphabet;
        _inputName = session.InputMetadata.Keys.First();
        _inW = inW;
        _inH = inH;
    }

    public static YoloCharReader? TryCreate(string path, ILogger logger)
    {
        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(path);
            string input = session.InputMetadata.Keys.First();
            var dims = session.InputMetadata[input].Dimensions;                       // [1, 3, H, W]
            int inH = dims.Length >= 4 && dims[2] > 0 ? dims[2] : 320;
            int inW = dims.Length >= 4 && dims[3] > 0 ? dims[3] : 320;

            string alphabet = LegacyAlphabet;
            var sidecar = Path.ChangeExtension(path, ".charset.txt");
            if (File.Exists(sidecar))
            {
                var chars = string.Concat(File.ReadAllLines(sidecar).Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => char.ToUpperInvariant(l[0])));
                if (chars.Length > 0) alphabet = chars;
            }

            var outDims = session.OutputMetadata.Values.First().Dimensions;           // [1, 4 + so_lop, N]
            if (outDims.Length == 3 && outDims[1] > 0 && outDims[1] != 4 + alphabet.Length && outDims[2] != 6)
            {
                logger.LogWarning("Model do ky tu '{Path}' co {N} lop, can {Need} lop theo bang ky tu bien so — bo qua model nay.",
                    path, outDims[1] - 4, alphabet.Length);
                session.Dispose();
                return null;
            }

            logger.LogInformation("Da nap model do ky tu YOLO: {Path} (dau vao {W}x{H}, {N} ky tu)", path, inW, inH, alphabet.Length);
            return new YoloCharReader(session, inW, inH, alphabet);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            logger.LogWarning(ex, "Tim thay model do ky tu '{Path}' nhung khong nap duoc — dung CNN phan loai ky tu.", path);
            return null;
        }
    }

    /// <summary>Quét lại với ngưỡng thấp hơn ở vùng nghi rơi ký tự (khoảng trống lớn trong dãy, hoặc đầu/cuối dãy).</summary>
    public volatile bool FillGaps = true;
    private const float GapRescanConfidence = 0.10f;
    private const float InteriorGapFactor = 1.7f;   // khoảng cách tâm-tâm > 1,7 bước ký tự thường (dấu '-' và '.' chỉ ~1,3–1,5) = nghi rơi ký tự
    private const float EdgeGapFactor = 1.0f;       // chỗ trống ở đầu/cuối ≥ 1 bước ký tự = đủ chỗ cho 1 ký tự
    private const float EdgeSearchFactor = 1.8f;
    private const double GapFillWeight = 0.85;      // lần đọc có ký tự bổ sung kém tin hơn lần đọc gốc
    private const int MaxPlateChars = 10;

    /// <summary>
    /// Đọc 1 ảnh biển (BGR). Trả về true nếu ký tự chạm mép trái/phải ảnh cắt (khung biển nghi bị cụt — phiếu bị giảm trọng số).
    /// Phiếu hợp lệ được cộng vào <paramref name="votes"/>; toạ độ dải ký tự = toạ độ ảnh cắt + (originX, originY).
    /// Nếu dãy ký tự có khoảng trống bất thường, quét lại ngưỡng thấp và bỏ THÊM một phiếu cho chuỗi đã bổ sung ký tự
    /// (phiếu gốc vẫn giữ — nếu ký tự bổ sung là nhiễu thì chuỗi gốc vẫn thắng nhờ hệ số cấu trúc).
    /// </summary>
    public bool CollectVotes(Mat plateBgr, Dictionary<string, PlateVote> votes, double weightScale = 1.0, int originX = 0, int originY = 0, PlateColor color = PlateColor.Unknown)
    {
        if (plateBgr.Empty()) return false;

        var dets = Detect(plateBgr, BoxConfidence);
        if (dets.Count < 3) return false;

        float margin = Math.Max(2f, 0.01f * plateBgr.Width);
        bool edgeCut = dets.Min(d => d.X1) <= margin || dets.Max(d => d.X2) >= plateBgr.Width - margin;
        double conf = dets.Average(d => d.Score);     // độ tin cậy tính trên các ký tự gốc (ký tự bổ sung có điểm thấp theo thiết kế)

        AddReadVote(dets, conf, votes, weightScale, originX, originY, edgeCut, 1.0, false, color);

        if (FillGaps)
        {
            var filled = FillMissingChars(plateBgr, dets);
            if (filled is not null)
                AddReadVote(filled, conf, votes, weightScale, originX, originY, edgeCut, GapFillWeight, true, color);
        }
        return edgeCut;
    }

    private void AddReadVote(List<Det> dets, double conf, Dictionary<string, PlateVote> votes, double weightScale,
                             int originX, int originY, bool edgeCut, double extraWeight, bool filled, PlateColor color)
    {
        if (conf < MinCharConfidence) return;

        var rows = GroupRows(dets);
        var texts = rows.Select(r => string.Concat(r.Select(d => _alphabet[d.Cls]))).ToList();
        string raw = string.Concat(texts);
        var lineLengths = texts.Count == 2 ? new[] { texts[0].Length, texts[1].Length } : null;
        var pretty = PlateNormalizer.Apply(raw, lineLengths, color);      // null / "" = không nhận diện được
        if (pretty is null) return;

        var key = Regex.Replace(pretty.ToUpperInvariant(), "[^A-Z0-9]", "");
        if (!votes.TryGetValue(key, out var vote))
            votes[key] = vote = new PlateVote { Pretty = pretty, Color = color };

        double weight = (1.0 + conf) * PlateNormalizer.Weight(pretty, color);
        if (key.Length >= 8 && Regex.IsMatch(key, @"\d{5}$")) weight += 0.2;
        weight *= TrustFactor * weightScale * extraWeight;
        if (edgeCut) weight *= 0.3;

        vote.Score += weight;
        vote.BestConfidence = Math.Max(vote.BestConfidence, (float)conf);
        vote.Sources |= ReaderSource.Yolo;
        vote.Reads++;
        vote.GapFilled |= filled;
        vote.AddRaw(ReaderSource.Yolo, filled ? raw + "(+)" : raw);

        float minX = dets.Min(d => d.X1), maxX = dets.Max(d => d.X2);
        float minY = dets.Min(d => d.Y1), maxY = dets.Max(d => d.Y2);
        if (!edgeCut && weight > vote.SpanWeight)
        {
            vote.HasSpan = true;
            vote.SpanWeight = weight;
            vote.SpanX1 = originX + minX;
            vote.SpanY1 = originY + minY;
            vote.SpanX2 = originX + maxX;
            vote.SpanY2 = originY + maxY;
        }
    }

    /// <summary>
    /// Phát hiện ký tự bị rơi bằng KHOẢNG CÁCH: ký tự trên biển cách nhau khá đều, nên khoảng trống giữa 2 ký tự liền kề lớn hơn bước thường
    /// (hoặc còn chỗ trống đủ cho 1 ký tự ở đầu/cuối dãy) nghĩa là model bỏ sót. Chạy lại model với ngưỡng thấp và chỉ nhận khung nằm ĐÚNG vùng
    /// trống, cùng chiều cao / cùng dòng với các ký tự còn lại, không chồng lên ký tự đã có. Trả về null nếu không bổ sung được gì.
    /// </summary>
    private List<Det>? FillMissingChars(Mat plate, List<Det> dets)
    {
        if (dets.Count < 3 || dets.Count >= MaxPlateChars) return null;

        var windows = new List<(float X1, float X2, float Cy, float H, int Max)>();
        foreach (var row in GroupRows(dets))
        {
            if (row.Count < 3) continue;
            var steps = new List<float>();
            for (int i = 0; i + 1 < row.Count; i++) steps.Add(row[i + 1].Cx - row[i].Cx);
            float step = Median(steps);
            if (step <= 1f) continue;
            float medH = Median(row.Select(d => d.H).ToList());
            float rowCy = Median(row.Select(d => d.Cy).ToList());

            for (int i = 0; i + 1 < row.Count; i++)
            {
                float g = row[i + 1].Cx - row[i].Cx;
                if (g > InteriorGapFactor * step)
                    windows.Add((row[i].X2, row[i + 1].X1, rowCy, medH, Math.Max(1, (int)Math.Round(g / step) - 1)));
            }
            if (row[0].X1 >= EdgeGapFactor * step)
                windows.Add((Math.Max(0f, row[0].X1 - EdgeSearchFactor * step), row[0].X1, rowCy, medH, 1));
            float trail = plate.Width - row[^1].X2;
            if (trail >= EdgeGapFactor * step)
                windows.Add((row[^1].X2, Math.Min(plate.Width, row[^1].X2 + EdgeSearchFactor * step), rowCy, medH, 1));
        }
        if (windows.Count == 0) return null;

        var low = Detect(plate, GapRescanConfidence);
        var result = new List<Det>(dets);
        bool added = false;
        foreach (var w in windows)
        {
            for (int n = 0; n < w.Max && result.Count < MaxPlateChars; n++)
            {
                var pick = low
                    .Where(d => d.Cx >= w.X1 && d.Cx <= w.X2)
                    .Where(d => Math.Abs(d.Cy - w.Cy) <= 0.35f * w.H)
                    .Where(d => d.H >= 0.8f * w.H && d.H <= 1.25f * w.H && d.W <= 1.2f * w.H)
                    .Where(d => !result.Any(k => IoU(k, d) > 0.2f || Inside(d, k) > 0.3f || Inside(k, d) > 0.3f))
                    .OrderByDescending(d => d.Score)
                    .FirstOrDefault();
                if (pick is null) break;
                result.Add(pick);
                added = true;
            }
        }
        return added ? result : null;
    }

    /// <summary>Chạy model trên ảnh biển → các ký tự (toạ độ theo ảnh biển), đã NMS và loại khung lạc kích thước.</summary>
    private List<Det> Detect(Mat plate, float conf)
    {
        float scale = Math.Min((float)_inW / plate.Width, (float)_inH / plate.Height);
        int newW = Math.Max(1, (int)(plate.Width * scale));
        int newH = Math.Max(1, (int)(plate.Height * scale));
        float padX = (_inW - newW) / 2f, padY = (_inH - newH) / 2f;

        using var resized = new Mat();
        Cv2.Resize(plate, resized, new Size(newW, newH), 0, 0, scale >= 1f ? InterpolationFlags.Cubic : InterpolationFlags.Area);
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

        var cands = new List<Det>();

        if (dims.Length == 3 && dims[1] >= 5 && dims[1] < dims[2])
        {
            // YOLOv8/11: [1, 4 + so_lop, N] (cx, cy, w, h, diem tung lop)
            int c = dims[1], n = dims[2];
            float maxW = 0f, maxH = 0f;
            for (int i = 0; i < n; i++) { maxW = Math.Max(maxW, output[0, 2, i]); maxH = Math.Max(maxH, output[0, 3, i]); }
            bool normalized = maxW <= 2f && maxH <= 2f;
            float sx = normalized ? _inW : 1f, sy = normalized ? _inH : 1f;

            for (int i = 0; i < n; i++)
            {
                float best = 0f; int cls = -1;
                for (int k = 4; k < c; k++)
                {
                    float s = output[0, k, i];
                    if (s > best) { best = s; cls = k - 4; }
                }
                if (best < conf || cls < 0 || cls >= _alphabet.Length) continue;

                float cx = output[0, 0, i] * sx, cy = output[0, 1, i] * sy, w = output[0, 2, i] * sx, h = output[0, 3, i] * sy;
                cands.Add(new Det
                {
                    X1 = (cx - w / 2 - padX) / scale, Y1 = (cy - h / 2 - padY) / scale,
                    X2 = (cx + w / 2 - padX) / scale, Y2 = (cy + h / 2 - padY) / scale,
                    Score = best, Cls = cls
                });
            }
        }
        else if (dims.Length == 3 && dims[2] >= 6)
        {
            // Da NMS san: [1, N, 6] = x1, y1, x2, y2, diem, lop
            for (int i = 0; i < dims[1]; i++)
            {
                float s = output[0, i, 4];
                int cls = (int)output[0, i, 5];
                if (s < conf || cls < 0 || cls >= _alphabet.Length) continue;
                cands.Add(new Det
                {
                    X1 = (output[0, i, 0] - padX) / scale, Y1 = (output[0, i, 1] - padY) / scale,
                    X2 = (output[0, i, 2] - padX) / scale, Y2 = (output[0, i, 3] - padY) / scale,
                    Score = s, Cls = cls
                });
            }
        }
        else
        {
            return new List<Det>();   // dang dau ra khong ho tro
        }

        foreach (var d in cands)
        {
            d.X1 = Math.Clamp(d.X1, 0, plate.Width); d.X2 = Math.Clamp(d.X2, 0, plate.Width);
            d.Y1 = Math.Clamp(d.Y1, 0, plate.Height); d.Y2 = Math.Clamp(d.Y2, 0, plate.Height);
        }
        cands.RemoveAll(d => d.W < 2f || d.H < 4f);

        // NMS KHONG phan biet lop: 1 ky tu chi duoc 1 khung (nhieu lop cung vuot nguong tren cung 1 net chu)
        var kept = new List<Det>();
        foreach (var d in cands.OrderByDescending(x => x.Score))
        {
            bool dup = kept.Any(k => IoU(k, d) > 0.5f || Inside(d, k) > 0.8f);
            if (!dup) kept.Add(d);
        }

        // Bo khung lac kich thuoc (hoa van, dong chu nho ben canh...) so voi chieu cao trung vi
        if (kept.Count >= 3)
        {
            float med = Median(kept.Select(d => d.H).ToList());
            kept.RemoveAll(d => d.H < 0.55f * med || d.H > 1.7f * med);
        }
        return kept;
    }

    /// <summary>
    /// Xếp ký tự thành 1 hoặc 2 dòng. Tách thành 2 dòng khi khoảng cách tâm-Y lớn nhất giữa 2 ký tự liền kề (sau khi sắp theo Y) vượt 0,7 chiều cao chữ
    /// và mỗi dòng có ≥ 2 ký tự — biển 1 dòng bị NGHIÊNG chỉ có các bước nhảy Y nhỏ nên không bị tách nhầm. Trong mỗi dòng sắp theo X.
    /// </summary>
    private static List<List<Det>> GroupRows(List<Det> dets)
    {
        var byY = dets.OrderBy(d => d.Cy).ToList();
        float medH = Median(byY.Select(d => d.H).ToList());
        int split = -1;
        float bestGap = 0f;
        for (int i = 0; i + 1 < byY.Count; i++)
        {
            float gap = byY[i + 1].Cy - byY[i].Cy;
            if (gap > bestGap) { bestGap = gap; split = i; }
        }

        var rows = new List<List<Det>>();
        if (split >= 1 && byY.Count - (split + 1) >= 2 && bestGap > 0.7f * medH)
        {
            rows.Add(byY.Take(split + 1).OrderBy(d => d.Cx).ToList());
            rows.Add(byY.Skip(split + 1).OrderBy(d => d.Cx).ToList());
        }
        else
        {
            rows.Add(byY.OrderBy(d => d.Cx).ToList());
        }
        return rows;
    }

    private static float Median(List<float> v)
    {
        v.Sort();
        int n = v.Count;
        return n == 0 ? 0f : n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2f;
    }

    private static float IoU(Det a, Det b)
    {
        float ix = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        float iy = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        float inter = ix * iy;
        float union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Phần diện tích của <paramref name="a"/> nằm trong <paramref name="b"/>.</summary>
    private static float Inside(Det a, Det b)
    {
        float ix = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        float iy = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        float area = a.W * a.H;
        return area <= 0 ? 0 : ix * iy / area;
    }

    public void Dispose() => _session.Dispose();
}
