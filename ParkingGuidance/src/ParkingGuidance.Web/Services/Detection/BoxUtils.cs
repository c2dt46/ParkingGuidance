using OpenCvSharp;
using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Detection;

public static class BoxUtils
{
    /// <summary>
    /// Chuyển 1 BoundingBox (có thể vượt ra ngoài ảnh do sai số của model) thành 1 Rect
    /// hợp lệ, nằm gọn trong kích thước ảnh gốc, an toàn để cắt (crop).
    /// </summary>
    public static Rect ClampToImage(Mat image, BoundingBox box)
    {
        int x1 = (int)Math.Max(0, box.X1);
        int y1 = (int)Math.Max(0, box.Y1);
        int x2 = (int)Math.Min(image.Width, box.X2);
        int y2 = (int)Math.Min(image.Height, box.Y2);

        int width = Math.Max(0, x2 - x1);
        int height = Math.Max(0, y2 - y1);

        return new Rect(x1, y1, width, height);
    }

    /// <summary>Tỉ lệ nới khung xe trước khi cắt để dò biển: khung xe của model hay cắt sát, biển (nhất là xe máy, biển ở mép dưới) dễ bị mất một phần.</summary>
    public const float PlateSearchExpand = 0.08f;

    /// <summary>Như <see cref="ClampToImage(Mat, BoundingBox)"/> nhưng nới khung ra mỗi phía <paramref name="expand"/> × kích thước khung (rồi vẫn kẹp trong ảnh).</summary>
    public static Rect ClampToImage(Mat image, BoundingBox box, float expand)
    {
        float dx = box.Width * expand, dy = box.Height * expand;
        return ClampToImage(image, new BoundingBox { X1 = box.X1 - dx, Y1 = box.Y1 - dy, X2 = box.X2 + dx, Y2 = box.Y2 + dy });
    }

    /// <summary>Tỉ lệ giao/hợp (IoU) của 2 khung, 0-1 — dùng để biết xe có đứng yên giữa 2 khung hình không.</summary>
    public static float Iou(BoundingBox a, BoundingBox b)
    {
        float x1 = Math.Max(a.X1, b.X1);
        float y1 = Math.Max(a.Y1, b.Y1);
        float x2 = Math.Min(a.X2, b.X2);
        float y2 = Math.Min(a.Y2, b.Y2);
        float inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}
