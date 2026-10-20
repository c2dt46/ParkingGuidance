using OpenCvSharp;
using ParkingGuidance.Web.Models;
using ParkingGuidance.Web.Services.Anpr;
using ParkingGuidance.Web.Services.Detection;

namespace ParkingGuidance.Web.Services.Camera;

/// <summary>
/// Phân tích 1 khung hình theo 2 nhánh SONG SONG trên cả khung: (1) dò xe, (2) dò khung biển số. Sau đó ghép biển với xe:
/// chỉ biển có phần nằm trong khung xe mới hợp lệ — biển nằm hoàn toàn ngoài mọi xe bị bỏ. Chỉ khung biển đã ghép được mới đem đi đọc ký tự,
/// nên xe không có biển không còn phải chạy các bước tìm biển dự phòng (chậm).
/// </summary>
public static class FrameAnalysis
{
    /// <summary>Dò xe và dò khung biển cùng lúc. Toạ độ trả về theo ảnh GỐC (đã quy đổi nếu ảnh được thu nhỏ theo ProcessMaxSide).</summary>
    public static (List<DetectionResult> Vehicles, List<BoundingBox> Plates) DetectParallel(
        IVehicleDetectionService detector, IPlateRecognitionService anpr, Mat frame,
        SystemSettings settings, bool detectPlates, float? vehicleConf = null)
    {
        int maxSide = settings.ProcessMaxSide ?? 0;
        int longSide = Math.Max(frame.Width, frame.Height);
        double scale = maxSide > 0 && longSide > maxSide ? maxSide / (double)longSide : 1.0;

        Mat? small = null;
        try
        {
            Mat src = frame;
            if (scale < 1.0)
            {
                small = new Mat();
                Cv2.Resize(frame, small, new Size(), scale, scale, InterpolationFlags.Area);
                src = small;
            }

            var vehTask = Task.Run(() => detector.Detect(src, vehicleConf));
            Task<List<BoundingBox>>? plateTask = detectPlates ? Task.Run(() => anpr.DetectPlateBoxes(src)) : null;

            // Phai doi XONG ca hai truoc khi giai phong anh thu nho (du mot nhanh loi)
            try { Task.WaitAll(plateTask is null ? new Task[] { vehTask } : new Task[] { vehTask, plateTask }); }
            catch (AggregateException) { /* xu ly tung nhanh ben duoi */ }

            if (vehTask.IsFaulted) throw vehTask.Exception!.InnerException ?? vehTask.Exception;
            var vehicles = vehTask.Result;
            // Nhanh do bien loi thi coi nhu khong co khung bien (xe van duoc ghi nhan nhu binh thuong)
            var plates = plateTask is not null && plateTask.IsCompletedSuccessfully ? plateTask.Result : new List<BoundingBox>();

            if (scale < 1.0)
            {
                float k = (float)(1.0 / scale);
                foreach (var v in vehicles) Scale(v.Box, k);
                foreach (var p in plates) Scale(p, k);
            }
            return (vehicles, plates);
        }
        finally
        {
            small?.Dispose();
        }
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
