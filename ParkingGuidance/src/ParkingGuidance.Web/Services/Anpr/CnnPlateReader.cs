using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Đọc chữ trên biển số: tách ký tự bằng thành phần liên thông (gom dòng theo toạ độ Y, loại khung viền, cắt khối ký tự
/// dính nhau) rồi phân loại TỪNG KÝ TỰ bằng CNN 28x28 của repo buiquangmanhhp1999/License-Plate-Recognition
/// (đã chuyển sang ONNX: models/char_cnn.onnx, 32 lớp = 21 chữ + 10 số + "nền").
/// Kết quả được chuẩn hoá bằng <see cref="VietnamPlateFormatter"/> và đưa vào cùng hệ bỏ phiếu như Tesseract.
/// </summary>
public sealed class CnnPlateReader : IDisposable
{
    private const string Alphabet = "ABCDEFGHKLMNPRSTUVXYZ0123456789"; // chi so 31 = nen/nhieu
    private const int BackgroundClass = 31;
    private const double LowContrastStdDev = 45.0;

    private readonly InferenceSession _session;
    private readonly string _inputName;

    private CnnPlateReader(InferenceSession session)
    {
        _session = session;
        _inputName = session.InputMetadata.Keys.First();
    }

    public static CnnPlateReader? TryCreate(string path, ILogger logger)
    {
        try
        {
            var r = new CnnPlateReader(new InferenceSession(path));
            logger.LogInformation("Da nap CNN phan loai ky tu bien so: {Path}", path);
            return r;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Khong nap duoc CNN ky tu {Path} — dung Tesseract.", path);
            return null;
        }
    }

    private sealed class Cand : IDisposable
    {
        public int X, Y, W, H;
        public Mat Mask = new();   // ky tu = 255, kich thuoc W x H
        public void Dispose() => Mask.Dispose();
    }

    /// <summary>Độ tin cậy TRUNG BÌNH tối thiểu của các ký tự trong 1 lần đọc (softmax 0–1); thấp hơn thì bỏ lần đọc đó. Đặt từ cài đặt chung.</summary>
    public volatile float MinCharConfidence;

    /// <summary>Chiều cao (px) ảnh biển được phóng/thu về trước khi đọc ký tự. Đặt từ cài đặt chung.</summary>
    public volatile int TargetHeight = 110;

    /// <summary>Đọc 1 ảnh biển (BGR) theo 4 kiểu nhị phân hoá, mỗi kiểu thành 1 phiếu nếu hợp định dạng biển VN.
    /// <paramref name="weightScale"/>: hệ số nhân trọng số phiếu (&lt; 1 cho lần đọc trên khung đã nới rộng — dễ lẫn nền xe nên chỉ để bổ trợ).
    /// Trả về true nếu có ký tự CHẠM MÉP TRÁI/PHẢI ảnh cắt (dấu hiệu khung biển bị cụt) — phiếu của lần đọc đó bị giảm trọng số.</summary>
    public bool CollectVotes(Mat plateBgr, Dictionary<string, PlateVote> votes, double weightScale = 1.0, int originX = 0, int originY = 0, PlateColor color = PlateColor.Unknown)
    {
        bool anyEdgeCut = false;
        if (plateBgr.Empty()) return false;

        using var gray0 = new Mat();
        Cv2.CvtColor(plateBgr, gray0, ColorConversionCodes.BGR2GRAY);
        double f = Math.Clamp(TargetHeight / (double)Math.Max(1, gray0.Height), 1.0, 6.0);
        using var gray = new Mat();
        Cv2.Resize(gray0, gray, new Size(), f, f, InterpolationFlags.Cubic);

        Cv2.MeanStdDev(gray, out _, out var sd);
        if (sd.Val0 < LowContrastStdDev)
        {
            using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
            using var eq = new Mat();
            clahe.Apply(gray, eq);
            eq.CopyTo(gray);
        }

        foreach (bool adaptive in new[] { false, true })
        {
            foreach (bool invert in new[] { false, true })
            {
                using var binary = Binarize(gray, adaptive, invert);
                var read = ReadVariant(binary);
                if (read is null) continue;
                var (texts, conf, edgeCut, span) = read.Value;
                anyEdgeCut |= edgeCut;
                if (conf < MinCharConfidence) continue;     // ky tu doc khong chac chan -> khong tinh phieu

                string raw = string.Concat(texts);
                var lineLengths = texts.Count == 2 ? new[] { texts[0].Length, texts[1].Length } : null;
                var pretty = PlateNormalizer.Apply(raw, lineLengths, color);      // ham cua nguoi dung: null / "" = khong nhan dien duoc
                if (pretty is null) continue;

                var key = Regex.Replace(pretty.ToUpperInvariant(), "[^A-Z0-9]", "");
                if (!votes.TryGetValue(key, out var vote))
                    votes[key] = vote = new PlateVote { Pretty = pretty, Color = color };

                double weight = (1.0 + conf) * PlateNormalizer.Weight(pretty, color);
                if (key.Length >= 8 && Regex.IsMatch(key, @"\d{5}$")) weight += 0.2; // uu tien duoi 5 so (doc thieu so cuoi la loi hay gap)
                weight *= weightScale;
                if (edgeCut) weight *= 0.3;                                          // ky tu cham mep -> khung bien co the bi cut, doc de bi sai/thieu
                vote.Score += weight;
                vote.BestConfidence = Math.Max(vote.BestConfidence, (float)conf);
                vote.Sources |= ReaderSource.Cnn;
                vote.Reads++;
                vote.AddRaw(ReaderSource.Cnn, raw);

                // Ghi lai dai ky tu (toa do anh goc cua nguoi goi = anh cat + origin) tu lan doc tot nhat, uu tien lan doc khong cham mep
                if (span.Width > 0 && !edgeCut && weight > vote.SpanWeight)
                {
                    vote.HasSpan = true;
                    vote.SpanWeight = weight;
                    vote.SpanX1 = originX + span.X / f;
                    vote.SpanY1 = originY + span.Y / f;
                    vote.SpanX2 = originX + (span.X + span.Width) / f;
                    vote.SpanY2 = originY + (span.Y + span.Height) / f;
                }
            }
        }
        return anyEdgeCut;
    }

    /// <summary>Chữ = 255, nền = 0.</summary>
    private static Mat Binarize(Mat gray, bool adaptive, bool invert)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(3, 3), 0);
        var binary = new Mat();
        if (!adaptive)
            Cv2.Threshold(blur, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        else
        {
            int block = Math.Max(15, (Math.Min(gray.Rows, gray.Cols) / 2) | 1);
            Cv2.AdaptiveThreshold(blur, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, block, 8);
        }
        if (invert) Cv2.BitwiseNot(binary, binary);
        return binary;
    }

    private (List<string> Texts, double Conf, bool EdgeCut, Rect Span)? ReadVariant(Mat binaryIn)
    {
        using var binary = StripFrame(binaryIn);
        var cands = Components(binary);
        try
        {
            var lines = GroupLines(cands);
            if (lines.Count == 0) return null;

            var texts = new List<string>();
            var confs = new List<float>();
            int sx1 = int.MaxValue, sy1 = int.MaxValue, sx2 = int.MinValue, sy2 = int.MinValue;   // dai ky tu that su duoc doc (bo qua lop "nen")
            foreach (var line in lines)
            {
                var (ids, probs, altIds, altProbs) = Classify(line);
                var heights = line.Select(c => c.H).OrderBy(h => h).ToList();
                double medH = heights[heights.Count / 2];
                var sb = new StringBuilder();
                for (int i = 0; i < ids.Length; i++)
                {
                    int id = ids[i];
                    float pr = probs[i];
                    if (id == BackgroundClass)
                    {
                        // CNN hay xep ky tu mong/mo (nhat la "1") vao lop "nen". Thanh phan nay da qua loc kich thuoc va nam cung dong, neu cao ngang cac chu
                        // khac va co phuong an ky tu du tin thi nhan phuong an do thay vi lam rot ky tu (biển 29A-653.14: "1" bi bo -> doc thanh 29A6534).
                        if (line[i].H >= 0.75 * medH && altProbs[i] >= 0.25f) { id = altIds[i]; pr = altProbs[i]; }
                        else continue;
                    }
                    sb.Append(Alphabet[id]);
                    confs.Add(pr);
                    var cc = line[i];
                    sx1 = Math.Min(sx1, cc.X); sy1 = Math.Min(sy1, cc.Y);
                    sx2 = Math.Max(sx2, cc.X + cc.W); sy2 = Math.Max(sy2, cc.Y + cc.H);
                }
                if (sb.Length == 0) return null;
                texts.Add(sb.ToString());
            }

            // Ky tu ngoai cung cham mep trai/phai anh cat => khung bien nhieu kha nang bi cut (mat ky tu o dau/cuoi)
            int margin = Math.Max(2, (int)(0.01 * binary.Width));
            int minX = lines.Min(l => l.Min(c => c.X)), maxR = lines.Max(l => l.Max(c => c.X + c.W));
            bool edgeCut = minX <= margin || maxR >= binary.Width - margin;
            var span = sx2 > sx1 && sy2 > sy1 ? new Rect(sx1, sy1, sx2 - sx1, sy2 - sy1) : new Rect();
            return (texts, confs.Count > 0 ? confs.Average() : 0.0, edgeCut, span);
        }
        finally
        {
            foreach (var c in cands) c.Dispose();
        }
    }

    /// <summary>Có khung viền lớn bao quanh biển (hay dính vào ký tự sát mép) → xoá dải viền, giữ phần bên trong.</summary>
    private static Mat StripFrame(Mat binary)
    {
        int W = binary.Width, H = binary.Height;
        using var labels = new Mat();
        using var stats = new Mat();
        using var cents = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(binary, labels, stats, cents, PixelConnectivity.Connectivity8);

        Rect? best = null;
        for (int i = 1; i < n; i++)
        {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), h = stats.At<int>(i, 3), a = stats.At<int>(i, 4);
            if (w > 0.55 * W && h > 0.6 * H && a / (double)(w * h) < 0.45)
                if (best is null || w * h > best.Value.Width * best.Value.Height) best = new Rect(x, y, w, h);
        }
        if (best is null) return binary.Clone();

        var b = best.Value;
        int m = Math.Max(3, (int)(0.06 * b.Height));
        var inner = new Rect(b.X + m, b.Y + m, b.Width - 2 * m, b.Height - 2 * m);
        var result = Mat.Zeros(binary.Size(), MatType.CV_8UC1).ToMat();
        if (inner.Width > 0 && inner.Height > 0)
        {
            using var src = new Mat(binary, inner);
            using var dst = new Mat(result, inner);
            src.CopyTo(dst);
        }
        return result;
    }

    private static byte[] Bytes(Mat m)
    {
        using var c = m.IsContinuous() ? m.Reshape(1, m.Rows) : m.Clone();
        var data = new byte[c.Rows * c.Cols];
        Marshal.Copy(c.Data, data, 0, data.Length);
        return data;
    }

    /// <summary>Ứng viên ký tự (đặc, không dẹt ngang). Khối quá rộng gồm nhiều ký tự dính nhau được cắt theo cột ít mực.</summary>
    private static List<Cand> Components(Mat binary)
    {
        int W = binary.Width, H = binary.Height;
        using var labels = new Mat();
        using var stats = new Mat();
        using var cents = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(binary, labels, stats, cents, PixelConnectivity.Connectivity8);

        var pieces = new List<Cand>();
        for (int i = 1; i < n; i++)
        {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), h = stats.At<int>(i, 3), area = stats.At<int>(i, 4);
            if (area < 0.004 * H * H || h < 0.12 * H) continue;
            if (h > 0.9 * H && w > 0.8 * W) continue;               // khung vien bien

            using var lroi = new Mat(labels, new Rect(x, y, w, h));
            var mask = new Mat();
            Cv2.InRange(lroi, new Scalar(i), new Scalar(i), mask);

            if (w > 1.2 * h && h >= 0.3 * H)
            {
                // nhieu ky tu dinh nhau -> cat theo cot it muc
                var bytes = Bytes(mask);
                var ink = new bool[w];
                for (int cx = 0; cx < w; cx++)
                {
                    int cnt = 0;
                    for (int ry = 0; ry < h; ry++) if (bytes[ry * w + cx] != 0) cnt++;
                    ink[cx] = cnt > 0.12 * h;
                }
                int x0 = -1;
                for (int cx = 0; cx <= w; cx++)
                {
                    bool on = cx < w && ink[cx];
                    if (on && x0 < 0) x0 = cx;
                    if (!on && x0 >= 0)
                    {
                        int minY = h, maxY = -1;
                        for (int ry = 0; ry < h; ry++)
                            for (int xx = x0; xx < cx; xx++)
                                if (bytes[ry * w + xx] != 0) { minY = Math.Min(minY, ry); maxY = Math.Max(maxY, ry); break; }
                        if (maxY >= minY)
                        {
                            var c = new Cand { X = x + x0, Y = y + minY, W = cx - x0, H = maxY - minY + 1 };
                            using var sub = new Mat(mask, new Rect(x0, minY, c.W, c.H));
                            c.Mask = sub.Clone();
                            pieces.Add(c);
                        }
                        x0 = -1;
                    }
                }
                mask.Dispose();
            }
            else
            {
                pieces.Add(new Cand { X = x, Y = y, W = w, H = h, Mask = mask });
            }
        }

        var keep = new List<Cand>();
        foreach (var c in pieces)
        {
            double fill = Cv2.CountNonZero(c.Mask) / (double)(c.W * c.H);
            if (c.H >= 0.12 * H && fill >= 0.25 && c.W <= 1.2 * c.H) keep.Add(c);
            else c.Dispose();
        }
        return keep;
    }

    // ---------------------------------------------------------------------------------------------------------
    // GOM DONG (sua loi lan ky tu giua 2 hang)
    //
    // Loi cu: GroupLines gom dong bang TRUNG BINH TRUOT theo toa do Y. Cac manh vien/nhieu CAO BAT THUONG (cao ~3 lan ky tu,
    // nam giua 2 hang) bi nhan nham la ky tu, lam chieu cao trung binh cua dong phinh ra => nguong 0.6*lh du lon de hut luon
    // ca hang duoi. Sau do bo loc chieu cao loai manh cao di, con 8 ky tu cua CA HAI hang nam trong 1 dong sap theo X ->
    // doc xen ke ("93521040"); VietnamPlateFormatter ep kieu theo vi tri nen 5 thanh S, D thanh 0 ("93S-210.40").
    //
    // Cach moi: (1) loai manh vien/nhieu theo dac trung HINH DANG; (2) tach hang bang KHOANG TRONG LON NHAT cua toa do Y da hieu
    // chinh theo do nghieng; (3) khop 2 DUONG TRUNG BINH song song qua tam ky tu cua tung hang, gan moi ky tu vao duong gan nhat
    // va loai ky tu nam xa ca hai duong; (4) tu choi doc khi chi thay 1 hang nhung bo cuc ky tu gan vuong (la bien 2 dong khong tach duoc).
    // ---------------------------------------------------------------------------------------------------------
    private const double GapRatio = 0.6;            // khoang cach 2 hang (tam-tam) toi thieu = 0.6 lan chieu cao ky tu
    private const int MaxSlopeSteps = 12;           // thu do doc +-0.12 (~7 do), buoc 0.01
    private const double MinSingleRowAspect = 2.2;  // 1 hang that phai dai (bbox rong/cao >= 2.2); bien vuong ~1.2-2.0

    private static double CX(Cand c) => c.X + c.W / 2.0;
    private static double CY(Cand c) => c.Y + c.H / 2.0;

    /// <summary>Loai manh vien/nhieu: thanh dung cao gap &gt;1.6 lan ky tu va manh, khoi to bat thuong (dien tich &gt;4 lan trung vi), nhieu qua thap.
    /// Khong ep moi ky tu cung chieu cao, nen hang tren/duoi khac co van doc duoc.</summary>
    private static (List<Cand> Kept, double RefH) SelectCharLike(List<Cand> cands)
    {
        if (cands.Count == 0) return (new List<Cand>(), 0.0);
        var hs = cands.Select(c => (double)c.H).ToList();
        if (cands.Count < 3) return (cands.ToList(), Median(hs.ToList()));

        // Chieu cao tham chieu = cum chieu cao dong nhat (hoa cum thi lay cum thap hon: manh nhieu thuong cao hon ky tu)
        double bestH = 0; int bestN = -1;
        foreach (var h in hs)
        {
            int n = hs.Count(k => k >= 0.75 * h && k <= 1.25 * h);
            if (n > bestN || (n == bestN && h < bestH)) { bestN = n; bestH = h; }
        }
        double refH = Median(hs.Where(k => k >= 0.75 * bestH && k <= 1.25 * bestH).ToList());
        double medArea = Median(cands.Select(c => (double)c.W * c.H).ToList());

        var kept = cands.Where(c =>
            !((c.H > 1.6 * refH && c.W < 0.35 * c.H)        // thanh dung cao + manh
              || (double)c.W * c.H > 4.0 * medArea           // khoi qua to
              || c.H < 0.5 * refH)).ToList();                // nhieu thap
        return (kept, kept.Count > 0 ? Median(kept.Select(c => (double)c.H).ToList()) : refH);
    }

    /// <summary>Tach hang: tim do doc sao cho khoang trong lon nhat giua cac tam Y (da hieu chinh) la lon nhat; neu &gt;= 0.6 chieu cao ky tu thi la 2 hang.</summary>
    private static List<List<Cand>> GroupRows(List<Cand> cands, double refH, out bool twoRows)
    {
        twoRows = false;
        var result = new List<List<Cand>>();
        int n = cands.Count;
        if (n < 2) return result;

        var cx = cands.Select(c => CX(c)).ToArray();
        var cy = cands.Select(c => CY(c)).ToArray();
        double mx = cx.Average();

        double bestGap = -1, bestS = 0;
        if (n >= 4)
        {
            var steps = new List<int> { 0 };                 // uu tien do doc gan 0 khi hoa
            for (int k = 1; k <= MaxSlopeSteps; k++) { steps.Add(k); steps.Add(-k); }
            foreach (int k in steps)
            {
                double s = k * 0.01;
                var ys = Enumerable.Range(0, n).Select(i => cy[i] - s * (cx[i] - mx)).OrderBy(v => v).ToArray();
                double g = 0;
                for (int i = 1; i <= n - 3; i++) g = Math.Max(g, ys[i + 1] - ys[i]);   // moi ben khe >= 2 ky tu
                if (g > bestGap + 1e-9) { bestGap = g; bestS = s; }
            }
        }

        if (bestGap >= GapRatio * refH)
        {
            var adj = Enumerable.Range(0, n).Select(i => cy[i] - bestS * (cx[i] - mx)).ToArray();
            var idx = Enumerable.Range(0, n).OrderBy(i => adj[i]).ToList();
            var rows = new List<List<int>>();
            var cur = new List<int> { idx[0] };
            for (int t = 1; t < idx.Count; t++)
            {
                if (adj[idx[t]] - adj[idx[t - 1]] >= GapRatio * refH) { rows.Add(cur); cur = new List<int>(); }
                cur.Add(idx[t]);
            }
            rows.Add(cur);
            rows = rows.Where(r => r.Count >= 2).ToList();
            if (rows.Count > 2)
                rows = rows.OrderByDescending(r => r.Count).Take(2).OrderBy(r => r.Min(i => adj[i])).ToList();
            twoRows = true;
            return rows.Select(r => r.Select(i => cands[i]).OrderBy(c => c.X).ToList()).ToList();
        }

        // Chi 1 hang: neu cac ky tu xep thanh khoi gan vuong thi thuc ra la bien 2 dong khong tach duoc -> tu choi, KHONG doc bay.
        int x1 = cands.Min(c => c.X), x2 = cands.Max(c => c.X + c.W);
        int y1 = cands.Min(c => c.Y), y2 = cands.Max(c => c.Y + c.H);
        if ((double)(x2 - x1) / Math.Max(1, y2 - y1) < MinSingleRowAspect) return result;
        result.Add(cands.OrderBy(c => c.X).ToList());
        return result;
    }

    /// <summary>Hai duong trung binh SONG SONG (chung do doc) qua tam ky tu cua tung hang (binh phuong toi thieu); gan lai moi ky tu vao
    /// duong gan nhat, loai ky tu cach xa ca hai duong (&gt; 0.5 chieu cao ky tu), roi loc chieu cao TRONG TUNG HANG.</summary>
    private static List<List<Cand>> RefineRows(List<List<Cand>> rows, double refH)
    {
        if (rows.Count != 2) return rows;
        var all = rows.SelectMany(r => r).ToList();
        var assign = new Dictionary<Cand, int>();
        for (int k = 0; k < 2; k++) foreach (var c in rows[k]) assign[c] = k;

        var a = new double[2];
        double s = 0;
        for (int it = 0; it < 3; it++)
        {
            var groups = new[] { all.Where(c => assign[c] == 0).ToList(), all.Where(c => assign[c] == 1).ToList() };
            if (groups[0].Count < 2 || groups[1].Count < 2) return rows;
            var mx = groups.Select(g => g.Average(c => CX(c))).ToArray();
            var my = groups.Select(g => g.Average(c => CY(c))).ToArray();
            double num = 0, den = 0;
            for (int k = 0; k < 2; k++)
                foreach (var c in groups[k]) { num += (CX(c) - mx[k]) * (CY(c) - my[k]); den += (CX(c) - mx[k]) * (CX(c) - mx[k]); }
            s = Math.Clamp(den > 1e-6 ? num / den : 0.0, -0.25, 0.25);
            a[0] = my[0] - s * mx[0];
            a[1] = my[1] - s * mx[1];
            if (a[1] - a[0] < GapRatio * refH) return rows;     // 2 duong qua gan nhau -> khong tin
            foreach (var c in all)
                assign[c] = Math.Abs(CY(c) - (a[1] + s * CX(c))) < Math.Abs(CY(c) - (a[0] + s * CX(c))) ? 1 : 0;
        }

        var outRows = new[] { new List<Cand>(), new List<Cand>() };
        foreach (var c in all)
        {
            int k = assign[c];
            if (Math.Abs(CY(c) - (a[k] + s * CX(c))) <= 0.5 * refH) outRows[k].Add(c);
        }

        var res = new List<List<Cand>>();
        foreach (var r in outRows)
        {
            if (r.Count < 2) return rows;
            double med = Median(r.Select(c => (double)c.H).ToList());
            var f = r.Where(c => c.H >= 0.6 * med && c.H <= 1.5 * med).OrderBy(c => c.X).ToList();
            if (f.Count < 2) return rows;
            res.Add(f);
        }
        return res;
    }

    /// <summary>Gom ky tu thanh 1 hoac 2 dong (xem ghi chu phia tren). Tra ve cac ung vien da duoc chon, moi dong sap theo X, dong tren truoc.</summary>
    private static List<List<Cand>> GroupLines(List<Cand> cands)
    {
        var (kept, refH) = SelectCharLike(cands);
        var rows = GroupRows(kept, refH, out bool twoRows);
        if (rows.Count == 0) return rows;
        return twoRows ? RefineRows(rows, refH) : rows;
    }

    private static double Median(List<double> v)
    {
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
    }

    /// <summary>Đưa mặt nạ về ảnh vuông 28x28 (đệm 0, resize INTER_AREA như repo) rồi chạy CNN.</summary>
    /// <summary>Ids/Probs = lớp tốt nhất (có thể là "nền") và xác suất; AltIds/AltProbs = lớp KÝ TỰ tốt nhất (bỏ lớp "nền") và xác suất.</summary>
    private (int[] Ids, float[] Probs, int[] AltIds, float[] AltProbs) Classify(List<Cand> line)
    {
        int n = line.Count;
        var tensor = new DenseTensor<float>(new[] { n, 1, 28, 28 });
        for (int i = 0; i < n; i++)
        {
            var c = line[i];
            int s = Math.Max(c.W, c.H);
            using var sq = Mat.Zeros(new Size(s, s), MatType.CV_8UC1).ToMat();
            using (var dst = new Mat(sq, new Rect((s - c.W) / 2, (s - c.H) / 2, c.W, c.H)))
                c.Mask.CopyTo(dst);
            using var small = new Mat();
            Cv2.Resize(sq, small, new Size(28, 28), 0, 0, InterpolationFlags.Area);
            var b = Bytes(small);
            for (int p = 0; p < 784; p++) tensor[i, 0, p / 28, p % 28] = b[p];   // 0..255, khong chuan hoa (nhu repo)
        }

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var probs = results.First().AsTensor<float>();
        var ids = new int[n];
        var best = new float[n];
        var altIds = new int[n];
        var altBest = new float[n];
        for (int i = 0; i < n; i++)
        {
            int arg = 0; float mx = float.MinValue;
            int altArg = 0; float altMx = float.MinValue;
            for (int k = 0; k < 32; k++)
            {
                float p = probs[i, k];
                if (p > mx) { mx = p; arg = k; }
                if (k != BackgroundClass && p > altMx) { altMx = p; altArg = k; }
            }
            ids[i] = arg; best[i] = mx;
            altIds[i] = altArg; altBest[i] = altMx;
        }
        return (ids, best, altIds, altBest);
    }

    public void Dispose() => _session.Dispose();
}
