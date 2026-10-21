using OpenCvSharp;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Anpr;
using ParkingGuidance.Web.Services.Detection;

namespace ParkingGuidance.Web.Services.Camera;

/// <summary>
/// Phân tích 1 khung hình theo 2 bước TUẦN TỰ: (1) dò xe trên cả khung (có thể thu nhỏ theo ProcessMaxSide cho nhanh), (2) với từng xe cần đọc biển,
/// CẮT vùng xe từ ẢNH GỐC (nới ra ngoài khung xe một chút, <see cref="BoxUtils.PlateSearchExpand"/>) rồi dò khung biển trong vùng đó.
/// Dò trong vùng xe giúp biển to hơn nhiều so với dò trên cả khung hình bị thu về 416×416, nên ít bỏ sót biển ở xa; đồng thời bước dò biển
/// chỉ chạy cho xe thật sự cần đọc (xe đã chốt biển thì bỏ qua) nên tốn ít CPU hơn dò song song trên cả khung ở mọi lần xử lý.
/// Biển chỉ hợp lệ nếu thuộc về xe chứa nó nhiều nhất (xem <see cref="AssignPlates"/>).
/// </summary>
public static class FrameAnalysis
{
    /// <summary>Dò xe trên cả khung. Toạ độ trả về theo ảnh GỐC (đã quy đổi nếu ảnh được thu nhỏ theo ProcessMaxSide).</summary>
    public static List<DetectionResult> DetectVehicles(
        IVehicleDetectionService detector, Mat frame, SystemSettings settings, float? vehicleConf = null)
    {
        int maxSide = settings.ProcessMaxSide ?? 0;
        int longSide = Math.Max(frame.Width, frame.Height);
        double scale = maxSide > 0 && longSide > maxSide ? maxSide / (double)longSide : 1.0;
        if (scale >= 1.0) return detector.Detect(frame, vehicleConf);

        using var small = new Mat();
        Cv2.Resize(frame, small, new Size(), scale, scale, InterpolationFlags.Area);
        var vehicles = detector.Detect(small, vehicleConf);
        float k = (float)(1.0 / scale);
        foreach (var v in vehicles) Scale(v.Box, k);
        return vehicles;
    }

    /// <summary>
    /// Dò khung biển của xe thứ <paramref name="index"/>: cắt vùng xe (nới 1 chút) từ ảnh gốc <paramref name="frame"/>, chỉ chạy bộ dò biển trong vùng đó,
    /// đổi khung về toạ độ cả khung hình rồi chỉ nhận biển thuộc về xe này (không lấy nhầm biển của xe bên cạnh). Không có biển / lỗi: trả về null.
    /// </summary>
    public static BoundingBox? DetectPlateForVehicle(
        IPlateRecognitionService anpr, Mat frame, IReadOnlyList<BoundingBox> vehicles, int index, double minOverlap)
    {
        if (frame.Empty() || index < 0 || index >= vehicles.Count) return null;

        var rect = BoxUtils.ClampToImage(frame, vehicles[index], BoxUtils.PlateSearchExpand);
        if (rect.Width < 32 || rect.Height < 32) return null;

        List<BoundingBox> found;
        try
        {
            using var crop = new Mat(frame, rect);
            found = anpr.DetectPlateBoxes(crop);
        }
        catch (Exception)
        {
            return null;   // nhanh do bien loi thi coi nhu khong co khung bien (xe van duoc ghi nhan nhu binh thuong)
        }
        if (found.Count == 0) return null;

        foreach (var p in found)
        {
            p.X1 += rect.X; p.X2 += rect.X;
            p.Y1 += rect.Y; p.Y2 += rect.Y;
        }
        var owned = AssignPlates(vehicles, found, minOverlap);
        return owned.TryGetValue(index, out var plate) ? plate : null;
    }

    /// <summary>
    /// Dò xe rồi dò biển trong vùng TỪNG xe (cho các màn hình thử / mô phỏng, cần biết biển của mọi xe). Toạ độ theo ảnh gốc.
    /// Luồng chạy thật (<c>CameraFrameProcessor</c>) không dùng hàm này mà chỉ dò biển cho xe thật sự cần đọc.
    /// </summary>
    public static (List<DetectionResult> Vehicles, List<BoundingBox> Plates) DetectWithPlates(
        IVehicleDetectionService detector, IPlateRecognitionService anpr, Mat frame,
        SystemSettings settings, bool detectPlates, float? vehicleConf = null)
    {
        var vehicles = DetectVehicles(detector, frame, settings, vehicleConf);
        var plates = new List<BoundingBox>();
        if (detectPlates && vehicles.Count > 0)
        {
            var boxes = vehicles.Select(v => v.Box).ToList();
            double minOverlap = settings.PlateMinOverlap ?? 0.1f;
            for (int i = 0; i < boxes.Count; i++)
            {
                var plate = DetectPlateForVehicle(anpr, frame, boxes, i, minOverlap);
                if (plate is not null) plates.Add(plate);
            }
        }
        return (vehicles, plates);
    }

    private static void Scale(BoundingBox b, float k)
    {
        b.X1 *= k; b.Y1 *= k; b.X2 *= k; b.Y2 *= k;
    }

    /// <summary>Phần diện tích của khung biển nằm trong khung xe (0–1).</summary>
    public static double PlateInsideRatio(BoundingBox vehicle, BoundingBox plate)
    {
        double ix = Math.Max(0, Math.Min(vehicle.X2, plate.X2) - Math.Max(vehicle.X1, plate.X1));
        double iy = Math.Max(0, Math.Min(vehicle.Y2, plate.Y2) - Math.Max(vehicle.Y1, plate.Y1));
        double area = Math.Max(1e-6, (double)plate.Width * plate.Height);
        return ix * iy / area;
    }

    /// <summary>
    /// Ghép khung biển với xe: mỗi biển thuộc về xe chứa nó nhiều nhất (hoà thì xe nhỏ hơn), mỗi xe lấy 1 biển (phần nằm trong xe lớn nhất, hoà thì biển to hơn).
    /// Biển có tỉ lệ nằm trong mọi xe &lt; <paramref name="minOverlap"/> bị bỏ (không hợp lệ). Trả về: chỉ số xe → khung biển.
    /// </summary>
    public static Dictionary<int, BoundingBox> AssignPlates(
        IReadOnlyList<BoundingBox> vehicles, IReadOnlyList<BoundingBox> plates, double minOverlap)
    {
        var result = new Dictionary<int, BoundingBox>();
        if (vehicles.Count == 0 || plates.Count == 0) return result;
        double thr = Math.Max(minOverlap, 0.0001);   // 0 = tat luat, nhung van chi ghep voi xe co chong lan that su

        var perVehicle = new Dictionary<int, List<(BoundingBox Plate, double Ratio)>>();
        foreach (var plate in plates)
        {
            int bestVeh = -1;
            double bestRatio = 0, bestArea = double.MaxValue;
            for (int i = 0; i < vehicles.Count; i++)
            {
                double r = PlateInsideRatio(vehicles[i], plate);
                if (r < thr) continue;
                double area = (double)vehicles[i].Width * vehicles[i].Height;
                if (r > bestRatio + 1e-9 || (Math.Abs(r - bestRatio) <= 1e-9 && area < bestArea))
                {
                    bestVeh = i; bestRatio = r; bestArea = area;
                }
            }
            if (bestVeh < 0) continue;
            if (!perVehicle.TryGetValue(bestVeh, out var list)) perVehicle[bestVeh] = list = new();
            list.Add((plate, bestRatio));
        }

        foreach (var (idx, list) in perVehicle)
            result[idx] = list.OrderByDescending(x => x.Ratio).ThenByDescending(x => (double)x.Plate.Width * x.Plate.Height).First().Plate;
        return result;
    }
}
