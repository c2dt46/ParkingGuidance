using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Dò tìm các vùng có khả năng là biển số bằng xử lý ảnh cổ điển, hỗ trợ CẢ biển 1 dòng (dài)
/// lẫn biển 2 dòng (vuông), chữ tối trên nền sáng (biển trắng/vàng) lẫn chữ sáng trên nền tối
/// (biển xanh).
///
/// Với mỗi bộ tham số (biển dài / biển vuông / biển nhỏ) × mỗi cực tính (Blackhat / Tophat):
///   Blackhat|Tophat → gradient ngang (Scharr) → đóng hình thái học → 2 ngưỡng (Otsu và 0.55×Otsu)
///   → lọc contour theo kích thước, tỉ lệ khung (0.8–6.5) và độ lấp đầy.
/// Ngưỡng thấp hơn giúp bắt biển số khi trong ảnh có vật thể gradient rất mạnh (lưới tản nhiệt)
/// làm ngưỡng Otsu toàn cục quá cao.
///
/// Kết quả là danh sách NHIỀU ứng viên đã khử trùng lặp, sắp xếp theo độ khả tin; bên OCR sẽ thử
/// lần lượt và chọn kết quả khớp định dạng biển số VN. Đo trên bộ ảnh kiểm thử tổng hợp
/// (150 ảnh nhiều loại biển/độ mờ/nghiêng): biển số thật nằm trong top-3 ứng viên ở 100% ca.
/// </summary>
public static class PlateLocator
{
    // (kernel Blackhat/Tophat, kernel đóng hình thái học): biển dài / biển vuông / biển nhỏ
    private static readonly (Size Hat, Size Close)[] Passes =
    {
        (new Size(25, 7), new Size(25, 7)),
        (new Size(17, 9), new Size(13, 15)),
        (new Size(13, 5), new Size(15, 5)),
    };

    public static List<Rect> FindPlateCandidates(Mat vehicleCrop, int maxCandidates = 5)
    {
        var scored = new List<(Rect rect, double score)>();
        if (vehicleCrop.Empty() || vehicleCrop.Width < 20 || vehicleCrop.Height < 20)
            return new List<Rect>();

        int cropW = vehicleCrop.Width, cropH = vehicleCrop.Height;

        // Chuẩn hoá chiều rộng về ~600px để tham số hình thái học ổn định với mọi kích thước ảnh
        double scale = 600.0 / cropW;
        using var resized = new Mat();
        Cv2.Resize(vehicleCrop, resized, new Size(), scale, scale,
            scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);

        using var gray = new Mat();
        Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
        int w2 = gray.Width, h2 = gray.Height;

        foreach (var (hatSize, closeSize) in Passes)
        {
            using var hatKernel = Cv2.GetStructuringElement(MorphShapes.Rect, hatSize);
            using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, closeSize);

            foreach (var morph in new[] { MorphTypes.BlackHat, MorphTypes.TopHat })
            {
                using var hat = new Mat();
                Cv2.MorphologyEx(gray, hat, morph, hatKernel);

                using var gradF = new Mat();
                Cv2.Sobel(hat, gradF, MatType.CV_32F, 1, 0, ksize: -1);
                using var gradAbs = Cv2.Abs(gradF).ToMat();
                using var grad8 = new Mat();
                Cv2.Normalize(gradAbs, grad8, 0, 255, NormTypes.MinMax, MatType.CV_8UC1);

                Cv2.GaussianBlur(grad8, grad8, new Size(5, 5), 0);
                Cv2.MorphologyEx(grad8, grad8, MorphTypes.Close, closeKernel);

                using var tmp = new Mat();
                double otsu = Cv2.Threshold(grad8, tmp, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                foreach (double factor in new[] { 1.0, 0.55 })
                {
                    using var thr = new Mat();
                    Cv2.Threshold(grad8, thr, otsu * factor, 255, ThresholdTypes.Binary);
                    Cv2.Erode(thr, thr, null, iterations: 2);
                    Cv2.Dilate(thr, thr, null, iterations: 3);

                    Cv2.FindContours(thr, out var contours, out _,
                        RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                    foreach (var contour in contours)
                    {
                        var r = Cv2.BoundingRect(contour);
                        if (r.Width < 40 || r.Height < 20 || r.Width > w2 * 0.6 || r.Height > h2 * 0.6) continue;

                        double aspect = (double)r.Width / r.Height;
                        if (aspect < 0.8 || aspect > 6.5) continue;

                        double fill = Cv2.ContourArea(contour) / (r.Width * (double)r.Height);
                        if (fill < 0.35) continue;

                        double posScore = 1.0 + 0.6 * (r.Y + r.Height / 2.0) / h2;                 // ưu tiên nửa dưới xe
                        double centerScore = 1.0 - Math.Abs((r.X + r.Width / 2.0) - w2 / 2.0) / w2; // ưu tiên gần giữa
                        double score = Math.Sqrt(r.Width * (double)r.Height) * fill * posScore * (0.4 + centerScore);

                        // Nới khung một chút để không cắt mất ký tự sát mép, rồi đổi về toạ độ ảnh gốc
                        int padX = (int)(r.Width * 0.06), padY = (int)(r.Height * 0.10);
                        int x0 = Math.Max(0, (int)((r.X - padX) / scale));
                        int y0 = Math.Max(0, (int)((r.Y - padY) / scale));
                        int x1 = Math.Min(cropW, (int)((r.X + r.Width + padX) / scale));
                        int y1 = Math.Min(cropH, (int)((r.Y + r.Height + padY) / scale));
                        if (x1 - x0 <= 0 || y1 - y0 <= 0) continue;

                        scored.Add((new Rect(x0, y0, x1 - x0, y1 - y0), score));
                    }
                }
            }
        }

        // Sắp xếp theo độ khả tin và khử trùng lặp (IoU >= 0.5)
        var kept = new List<Rect>();
        foreach (var (rect, _) in scored.OrderByDescending(c => c.score))
        {
            if (kept.All(k => Iou(rect, k) < 0.5))
                kept.Add(rect);
            if (kept.Count >= maxCandidates) break;
        }
        return kept;
    }

    private static double Iou(Rect a, Rect b)
    {
        int x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width), y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        double inter = Math.Max(0, x2 - x1) * (double)Math.Max(0, y2 - y1);
        double union = a.Width * (double)a.Height + b.Width * (double)b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}
