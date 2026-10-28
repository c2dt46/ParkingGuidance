using System.Text.RegularExpressions;
using OpenCvSharp;
using Tesseract;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>Kết quả bỏ phiếu cho 1 chuỗi biển số ứng viên.</summary>
public sealed class PlateVote
{
    public string Pretty { get; init; } = "";
    public double Score { get; set; }        // mỗi lần đọc trùng khớp cộng (1 + độ tin cậy)
    public float BestConfidence { get; set; } // độ tin cậy Tesseract cao nhất (0..1)

    /// <summary>Dải KÝ TỰ (không phải cả biển) của lần đọc CNN tốt nhất, theo toạ độ ảnh đưa vào Recognize — dùng để tính lại khung biển đúng
    /// khi bộ dò cho khung cụt. Chỉ có khi đọc bằng CNN.</summary>
    public bool HasSpan { get; set; }
    public double SpanX1 { get; set; }
    public double SpanY1 { get; set; }
    public double SpanX2 { get; set; }
    public double SpanY2 { get; set; }
    public double SpanWeight { get; set; }

    /// <summary>Các bộ đọc ĐỘC LẬP đã cho kết quả này (YOLO / CNN / CRNN / Tesseract). ≥ 2 cờ = có đồng thuận độc lập.</summary>
    public ReaderSource Sources { get; set; }
    /// <summary>Số lần đọc (mọi bộ đọc) ra đúng chuỗi này.</summary>
    public int Reads { get; set; }
    /// <summary>Chuỗi có ký tự được bổ sung nhờ quét lại vùng khoảng trống (YOLO).</summary>
    public bool GapFilled { get; set; }
    /// <summary>Màu nền biển của khung đã cho ra phiếu này (căn cứ chọn cú pháp khi chuẩn hoá / chấm điểm).</summary>
    public PlateColor Color { get; set; }

    private readonly List<string> _raws = new();
    /// <summary>Chuỗi THÔ của từng bộ đọc trước khi chuẩn hoá, dạng "Yolo:308-10370" — để ghi log và thống kê bộ đọc sai kiểu gì.</summary>
    public IReadOnlyList<string> Raws => _raws;
    public void AddRaw(ReaderSource src, string raw) => AddRawTagged(src + ":" + raw);
    public void AddRawTagged(string tagged) { if (_raws.Count < 16 && !_raws.Contains(tagged)) _raws.Add(tagged); }
}

/// <summary>
/// Đọc chữ trên 1 vùng biển số đã cắt, hỗ trợ biển 1 dòng và 2 dòng.
///
/// Vì sao không đưa thẳng ảnh vào Tesseract: ảnh thực tế có khung viền, nhiễu, chữ đậm dính nhau,
/// nền/chữ có thể sáng-tối ngược nhau. Quy trình ở đây:
///   1. Nhị phân hoá theo 4 biến thể (Otsu / ngưỡng thích ứng × 2 cực tính) — không đoán cực tính.
///   2. Chỉ giữ các thành phần liên thông thuộc vùng chữ (bỏ khung viền, bụi), GIỮ cả khối ký tự
///      dính nhau, rồi vẽ lại thành ảnh chữ đen nền trắng sạch.
///   3. OCR: PSM 1 dòng (biển dài) hoặc PSM khối (biển 2 dòng) + biến thể làm mảnh nét chữ.
///   4. Chuẩn hoá theo định dạng biển VN; chỉ kết quả hợp lệ mới được bỏ 1 phiếu.
/// Kết quả nhiều lần đọc trùng nhau có điểm cao hơn → giảm nhầm lẫn ngẫu nhiên từng lần.
/// </summary>
public sealed class PlateTextReader
{
    private readonly TesseractEngine _engine;

    /// <summary>Độ lệch chuẩn mức xám dưới ngưỡng này = ảnh biển số ít tương phản → áp CLAHE.</summary>
    private const double LowContrastStdDev = 45.0;

    public PlateTextReader(TesseractEngine engine) => _engine = engine;

    public void CollectVotes(Mat plateBgr, Dictionary<string, PlateVote> votes, PlateColor color = PlateColor.Unknown)
    {
        if (plateBgr.Empty()) return;

        using var gray0 = new Mat();
        Cv2.CvtColor(plateBgr, gray0, ColorConversionCodes.BGR2GRAY);

        // Phóng đại/thu nhỏ về chiều cao ~110px để ngưỡng kích thước ký tự ổn định
        double f = Math.Clamp(110.0 / Math.Max(1, gray0.Height), 1.0, 6.0);
        using var gray = new Mat();
        Cv2.Resize(gray0, gray, new Size(), f, f, InterpolationFlags.Cubic);

        // Anh it tuong phan (nguoc sang, ban dem, chói den pha, mo): can bang tuong phan cuc bo (CLAHE) truoc khi nhi phan hoa.
        // Chi ap dung khi anh thuc su phang (do lech chuan thap) de khong lam hong anh da tot.
        Cv2.MeanStdDev(gray, out _, out var stdDev);
        if (stdDev.Val0 < LowContrastStdDev)
        {
            using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
            using var equalized = new Mat();
            clahe.Apply(gray, equalized);
            equalized.CopyTo(gray);
        }

        foreach (bool adaptive in new[] { false, true })
        {
            foreach (bool invert in new[] { false, true })
            {
                using var binary = Binarize(gray, adaptive, invert);
                using var extracted = ExtractTextImage(binary);
                if (extracted is null) continue;
                using var clean = Deskew(extracted);

                bool twoLines = clean.Width / (double)Math.Max(1, clean.Height) < 2.2;

                foreach (bool thin in new[] { false, true })
                {
                    using var img = thin ? Thin(clean) : clean.Clone();
                    var (raw, conf, lineLengths) = OcrText(img, twoLines);
                    if (raw is null) continue;

                    var pretty = PlateNormalizer.Apply(raw, twoLines ? lineLengths : null, color);   // ham cua nguoi dung: null / "" = khong nhan dien duoc
                    if (pretty is null) continue;

                    var key = Regex.Replace(pretty.ToUpperInvariant(), "[^A-Z0-9]", "");
                    if (!votes.TryGetValue(key, out var vote))
                        votes[key] = vote = new PlateVote { Pretty = pretty, Color = color };

                    // Ma tinh la (khong nam trong danh sach thuong gap) thuong la doc sai 1-2 ky tu dau: giam trong so, khong loai han
                    double weight = (1.0 + conf) * PlateNormalizer.Weight(pretty, color);
                    vote.Score += weight;
                    vote.BestConfidence = Math.Max(vote.BestConfidence, conf);
                    vote.Sources |= ReaderSource.Tesseract;
                    vote.Reads++;
                    vote.AddRaw(ReaderSource.Tesseract, raw);
                }
            }
        }
    }

    /// <summary>Chữ = 255, nền = 0.</summary>
    private static Mat Binarize(Mat gray, bool adaptive, bool invert)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(3, 3), 0);

        var binary = new Mat();
        if (!adaptive)
        {
            Cv2.Threshold(blur, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        }
        else
        {
            int blockSize = Math.Max(15, (Math.Min(gray.Rows, gray.Cols) / 2) | 1);
            Cv2.AdaptiveThreshold(blur, binary, 255, AdaptiveThresholdTypes.GaussianC,
                ThresholdTypes.Binary, blockSize, 8);
        }

        if (invert) Cv2.BitwiseNot(binary, binary);
        return binary;
    }

    /// <summary>
    /// Giữ lại các thành phần liên thông thuộc vùng chữ và vẽ thành ảnh chữ đen nền trắng.
    /// Trả về null nếu không tìm thấy vùng chữ hợp lý.
    /// </summary>
    private static Mat? ExtractTextImage(Mat binary)
    {
        int H = binary.Rows, W = binary.Cols;

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids);

        // (nhan, x, y, w, h, do dac)
        var cand = new List<(int Id, int X, int Y, int W, int H, double Fill)>();
        for (int i = 1; i < n; i++)
        {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1);
            int w = stats.At<int>(i, 2), h = stats.At<int>(i, 3), area = stats.At<int>(i, 4);

            if (area < 0.004 * H * H) continue; // bui/nhieu qua nho
            double fill = area / (double)(w * h);
            cand.Add((i, x, y, w, h, fill));
        }

        // Loai net vien/trang tri: net mong (fill thap, ~5-10%) khac han ky tu dac (fill >= ~25%).
        // Quan trong hon so voi loc theo kich thuoc don thuan: khung vien co the co kich thuoc GAN
        // BANG ca tam anh nhung van la net mong, trong khi ky tu luon la khoi dac.
        var plausible = cand.Where(c => c.Fill >= 0.25 && c.W <= 4 * c.H).ToList();
        if (plausible.Count == 0) return null;

        // Gom thanh TUNG DONG theo vi tri Y truoc, roi moi loc theo chieu cao — dung chieu cao
        // TRUNG VI CUA TUNG DONG RIENG. Neu dung 1 nguong chung cho ca anh, dong co chu nho hon
        // (thuong la dong 2 tren bien 2 dong) se bi loai bo hoan toan — day chinh la loi khien
        // ANPR truoc day gan nhu luon that bai voi bien so 2 dong.
        plausible = plausible.OrderBy(c => c.Y + c.H / 2.0).ToList();
        var lines = new List<List<(int Id, int X, int Y, int W, int H, double Fill)>> { new() { plausible[0] } };
        for (int k = 1; k < plausible.Count; k++)
        {
            var c = plausible[k];
            double cy = c.Y + c.H / 2.0;
            var last = lines[^1];
            double lineCy = last.Average(x => x.Y + x.H / 2.0);
            double lineH = last.Average(x => (double)x.H);
            if (Math.Abs(cy - lineCy) < 0.7 * Math.Max(lineH, 1))
                last.Add(c);
            else
                lines.Add(new() { c });
        }

        // Bien so VN toi da 2 dong: neu phat hien nhieu hon (nhieu/tem/oc vit con sot sau loc fill),
        // chi giu 2 dong "dong" nhat (nhieu thanh phan nhat), sap lai theo thu tu tren-duoi.
        if (lines.Count > 2)
        {
            lines = lines.OrderByDescending(l => l.Count).Take(2)
                         .OrderBy(l => l.Average(c => c.Y)).ToList();
        }

        var chars = new List<(int Id, int X, int Y, int W, int H)>();
        foreach (var line in lines)
        {
            if (line.Count < 2) continue; // 1 thanh phan le loi (vd tem con sot) khong phai 1 dong chu
            double med = Median(line.Select(c => (double)c.H).ToList());
            foreach (var c in line)
            {
                if (c.H >= 0.5 * med && c.H <= 1.8 * med)
                    chars.Add((c.Id, c.X, c.Y, c.W, c.H));
            }
        }
        if (chars.Count == 0) return null;

        using var mask = new Mat(binary.Size(), MatType.CV_8UC1, Scalar.All(0));
        foreach (var c in chars)
        {
            using var one = new Mat();
            Cv2.InRange(labels, new Scalar(c.Id), new Scalar(c.Id), one);
            Cv2.BitwiseOr(mask, one, mask);
        }

        var bb = Cv2.BoundingRect(mask);
        if (bb.Width <= 0 || bb.Height <= 0) return null;

        int x0 = Math.Max(0, bb.X - 4), y0 = Math.Max(0, bb.Y - 4);
        int x1 = Math.Min(W, bb.X + bb.Width + 5), y1 = Math.Min(H, bb.Y + bb.Height + 5);
        using var roi = new Mat(mask, new OpenCvSharp.Rect(x0, y0, x1 - x0, y1 - y0));

        var result = new Mat();
        Cv2.BitwiseNot(roi, result);   // chu den tren nen trang
        return result;
    }

    /// <summary>Làm mảnh nét chữ (dilate ảnh chữ-đen-nền-trắng) để tách các ký tự dính nhau.</summary>
    private static Mat Thin(Mat clean)
    {
        var dst = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Dilate(clean, dst, kernel);
        return dst;
    }

    /// <summary>Chỉnh thẳng dòng chữ bị nghiêng (thử các góc -10..10°, chọn góc làm các hàng chữ
    /// "gọn" nhất — phương sai tổng pixel chữ theo từng hàng lớn nhất). Ảnh vào: chữ đen nền trắng.</summary>
    private static Mat Deskew(Mat clean)
    {
        using var mask = new Mat();
        Cv2.Threshold(clean, mask, 128, 255, ThresholdTypes.BinaryInv); // chu = 255 de tinh phuong sai

        int w = mask.Width, h = mask.Height;
        if (w < 10 || h < 10) return clean.Clone();

        using var small = w > 400 ? Resize(mask, 0.5) : mask.Clone();

        double bestVar = -1, bestAngle = 0;
        for (double a = -10; a <= 10.0001; a += 1.0)
        {
            using var rotated = RotateKeepSize(small, a);
            var rowSums = new double[rotated.Rows];
            for (int y = 0; y < rotated.Rows; y++)
                rowSums[y] = Cv2.Sum(rotated.Row(y)).Val0;
            double mean = rowSums.Average();
            double variance = rowSums.Select(v => (v - mean) * (v - mean)).Average();
            if (variance > bestVar) { bestVar = variance; bestAngle = a; }
        }

        if (Math.Abs(bestAngle) < 1.0) return clean.Clone();

        int pad = (int)(Math.Abs(Math.Sin(bestAngle * Math.PI / 180.0)) * w / 2) + 8;
        using var bordered = new Mat();
        Cv2.CopyMakeBorder(clean, bordered, pad, pad, 0, 0, BorderTypes.Constant, new Scalar(255));
        using var rot = RotateKeepSize(bordered, bestAngle, borderValue: 255);

        using var invRot = new Mat();
        Cv2.Threshold(rot, invRot, 128, 255, ThresholdTypes.BinaryInv);
        var bb = Cv2.BoundingRect(invRot);
        if (bb.Width <= 0 || bb.Height <= 0) return clean.Clone();

        int x0 = Math.Max(0, bb.X - 4), y0 = Math.Max(0, bb.Y - 4);
        int x1 = Math.Min(rot.Width, bb.X + bb.Width + 5), y1 = Math.Min(rot.Height, bb.Y + bb.Height + 5);
        return new Mat(rot, new OpenCvSharp.Rect(x0, y0, x1 - x0, y1 - y0)).Clone();
    }

    private static Mat Resize(Mat src, double factor)
    {
        var dst = new Mat();
        Cv2.Resize(src, dst, new Size(), factor, factor, InterpolationFlags.Area);
        return dst;
    }

    private static Mat RotateKeepSize(Mat src, double angleDeg, int borderValue = 0)
    {
        var m = Cv2.GetRotationMatrix2D(new Point2f(src.Width / 2f, src.Height / 2f), angleDeg, 1.0);
        var dst = new Mat();
        Cv2.WarpAffine(src, dst, m, src.Size(), InterpolationFlags.Cubic, borderValue: new Scalar(borderValue));
        return dst;
    }

    /// <summary>
    /// Chạy OCR và trả về (chuỗi đã ghép toàn bộ ký tự A-Z0-9, độ tin cậy, độ dài từng dòng).
    /// Độ dài từng dòng lấy từ chính ký tự xuống dòng (\n) mà Tesseract chèn vào giữa các dòng khi
    /// nhận diện ở chế độ SingleBlock (biển 2 dòng) — đây là thông tin THẬT từ phân tích của chính
    /// Tesseract, đáng tin hơn nhiều so với đoán cấu trúc chỉ dựa vào tổng độ dài chuỗi.
    /// </summary>
    private (string? Text, float Confidence, List<int>? LineLengths) OcrText(Mat clean, bool twoLines)
    {
        double target = twoLines ? 140.0 : 80.0;
        double s = target / Math.Max(1, clean.Height);

        using var scaled = new Mat();
        Cv2.Resize(clean, scaled, new Size(), s, s, InterpolationFlags.Cubic);
        using var bordered = new Mat();
        Cv2.CopyMakeBorder(scaled, bordered, 12, 12, 12, 12, BorderTypes.Constant, new Scalar(255));

        var png = bordered.ToBytes(".png");
        using var pix = Pix.LoadFromMemory(png);
        using var page = _engine.Process(pix, twoLines ? PageSegMode.SingleBlock : PageSegMode.SingleLine);

        var rawText = page.GetText();
        if (string.IsNullOrWhiteSpace(rawText)) return (null, 0f, null);

        // Tach theo dong that (Tesseract chen \n giua cac dong khi nhan dien SingleBlock), lam sach
        // tung dong (chi giu A-Z0-9) de lay dung do dai tung dong.
        var rawLines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => Regex.Replace(l.ToUpperInvariant(), "[^A-Z0-9]", ""))
            .Where(l => l.Length > 0)
            .ToList();

        var combined = string.Concat(rawLines);
        if (combined.Length == 0) return (null, 0f, null);

        var lineLengths = rawLines.Count == 2 ? rawLines.Select(l => l.Length).ToList() : null;
        return (combined, page.GetMeanConfidence(), lineLengths);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2.0;
    }
}
