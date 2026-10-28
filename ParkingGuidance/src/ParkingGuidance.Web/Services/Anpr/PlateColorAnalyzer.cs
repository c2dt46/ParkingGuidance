using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>Màu nền chủ đạo của biển số (theo quy định VN: trắng, vàng, xanh, đỏ).</summary>
public enum PlateColor
{
    Unknown = 0,
    /// <summary>Nền trắng, chữ đen: xe dân sự (cá nhân, doanh nghiệp). Biển ngoại giao / nước ngoài cũng nền trắng.</summary>
    White = 1,
    /// <summary>Nền vàng: xe kinh doanh vận tải (cùng cú pháp biển dân sự).</summary>
    Yellow = 2,
    /// <summary>Nền xanh, chữ trắng: cơ quan Đảng, Nhà nước, đơn vị sự nghiệp công lập...</summary>
    Blue = 3,
    /// <summary>Nền đỏ, chữ trắng: quân đội.</summary>
    Red = 4
}

/// <summary>
/// Xác định màu nền chủ đạo của biển số từ ảnh. Đếm điểm ảnh theo màu trong HSV (bỏ qua điểm tối = chữ đen / xám không rõ màu),
/// màu nhiều nhất là màu nền (chữ chiếm ít điểm ảnh hơn nền). Không đủ điểm ảnh có màu rõ hoặc không màu nào chiếm ≥ 40% → <see cref="PlateColor.Unknown"/>,
/// khi đó hệ thống dùng cú pháp tổng quát (không bị ép theo màu) — nên màu sai/mờ do ánh sáng chỉ làm giảm điểm chứ không loại biển.
/// </summary>
public static class PlateColorAnalyzer
{
    public readonly record struct Result(PlateColor Color, double Share);

    private const int MinCounted = 24;       // ít hơn số điểm ảnh có màu rõ này → không kết luận
    private const double MinShare = 0.40;    // màu chủ đạo phải chiếm ≥ 40% điểm ảnh có màu rõ

    /// <summary>Màu của biển nằm trong khung <paramref name="rect"/> của ảnh <paramref name="image"/> (BGR).</summary>
    public static Result Detect(Mat image, OpenCvSharp.Rect rect)
    {
        try
        {
            // Co vào 6% ngang / 12% dọc để bỏ khung biển và phần nền xung quanh (khung dò thường nới viền)
            int dx = (int)Math.Round(rect.Width * 0.06), dy = (int)Math.Round(rect.Height * 0.12);
            int x1 = Math.Clamp(rect.X + dx, 0, image.Width - 1);
            int y1 = Math.Clamp(rect.Y + dy, 0, image.Height - 1);
            int x2 = Math.Clamp(rect.X + rect.Width - dx, x1 + 1, image.Width);
            int y2 = Math.Clamp(rect.Y + rect.Height - dy, y1 + 1, image.Height);
            if (x2 - x1 < 8 || y2 - y1 < 4) return new Result(PlateColor.Unknown, 0);
            using var roi = new Mat(image, new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1));
            return Detect(roi);
        }
        catch
        {
            return new Result(PlateColor.Unknown, 0);
        }
    }

    /// <summary>Màu của ảnh biển (BGR, 3 kênh).</summary>
    public static Result Detect(Mat plateBgr)
    {
        if (plateBgr.Empty() || plateBgr.Channels() != 3 || plateBgr.Width < 8 || plateBgr.Height < 4)
            return new Result(PlateColor.Unknown, 0);

        using var small = new Mat();
        double sc = Math.Min(1.0, 96.0 / plateBgr.Width);
        if (sc < 1.0) Cv2.Resize(plateBgr, small, new OpenCvSharp.Size(), sc, sc, InterpolationFlags.Area);
        else plateBgr.CopyTo(small);

        using var hsv = new Mat();
        Cv2.CvtColor(small, hsv, ColorConversionCodes.BGR2HSV);
        var buf = new byte[checked((int)(hsv.Total() * 3))];
        Marshal.Copy(hsv.Data, buf, 0, buf.Length);

        int red = 0, blue = 0, yellow = 0, white = 0;
        for (int i = 0; i + 2 < buf.Length; i += 3)
        {
            int h = buf[i], s = buf[i + 1], v = buf[i + 2];      // OpenCV: H 0..179, S/V 0..255
            if (v < 60) continue;                                // tối: chữ đen / bóng đổ
            if (s <= 50)
            {
                if (v >= 100) white++;                           // trắng (sáng, ít bão hoà); xám tối bỏ qua
                continue;
            }
            if (s < 80) continue;                                // màu nhạt không rõ
            if (h <= 8 || h >= 170) red++;
            else if (h >= 14 && h <= 38) yellow++;
            else if (h >= 95 && h <= 135) blue++;
        }

        int counted = red + blue + yellow + white;
        if (counted < MinCounted) return new Result(PlateColor.Unknown, 0);

        var (color, n) = (PlateColor.White, white);
        if (yellow > n) (color, n) = (PlateColor.Yellow, yellow);
        if (blue > n) (color, n) = (PlateColor.Blue, blue);
        if (red > n) (color, n) = (PlateColor.Red, red);

        double share = n / (double)counted;
        return share >= MinShare ? new Result(color, share) : new Result(PlateColor.Unknown, share);
    }
}
