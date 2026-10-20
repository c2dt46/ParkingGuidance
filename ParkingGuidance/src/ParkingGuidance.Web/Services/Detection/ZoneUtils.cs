using ParkingGuidance.Web.Models;

namespace ParkingGuidance.Web.Services.Detection;

/// <summary>
/// Tính tỉ lệ diện tích khung xe (bounding box) nằm trong 1 vùng nhận diện (đa giác bất kỳ, kể cả
/// vùng nghiêng do góc camera). Ước lượng bằng cách lấy mẫu 1 lưới điểm đều trong khung xe rồi đếm
/// bao nhiêu điểm rơi vào trong đa giác (thuật toán ray-casting chuẩn) — đơn giản, không cần dựng
/// hình học clip đa giác, đủ chính xác cho mục đích quyết định "xe có thuộc ô đỗ này không".
///
/// Đã kiểm chứng bằng bộ test (hình chữ nhật thẳng, tràn biên, vùng nghiêng...): sai số dưới 3%
/// với lưới 20x20, dưới 6% với lưới 10x10 mặc định.
/// </summary>
public static class ZoneUtils
{
    /// <summary>Xe được coi là "thuộc vùng" khi tỉ lệ diện tích trong vùng ≥ ngưỡng này (mặc định:
    /// phần lớn = trên 50%).</summary>
    public const double DefaultMajorityThreshold = 0.5;

    /// <summary>
    /// Tính tỉ lệ (0-1) diện tích <paramref name="box"/> (toạ độ pixel, hệ trục ảnh
    /// <paramref name="frameWidth"/>x<paramref name="frameHeight"/>) nằm trong vùng
    /// <paramref name="zone"/> (toạ độ đỉnh lưu dạng % 0-100).
    /// </summary>
    public static double ComputeCoverageRatio(
        BoundingBox box, DetectionZone zone, int frameWidth, int frameHeight, int gridSize = 10)
    {
        if (zone.Points.Count < 3 || frameWidth <= 0 || frameHeight <= 0)
            return 0;

        var polygon = zone.Points
            .Select(p => (X: p.X / 100.0 * frameWidth, Y: p.Y / 100.0 * frameHeight))
            .ToArray();

        int inside = 0, total = 0;
        for (int i = 0; i < gridSize; i++)
        {
            for (int j = 0; j < gridSize; j++)
            {
                double px = box.X1 + (box.X2 - box.X1) * (i + 0.5) / gridSize;
                double py = box.Y1 + (box.Y2 - box.Y1) * (j + 0.5) / gridSize;
                total++;
                if (PointInPolygon(px, py, polygon)) inside++;
            }
        }
        return total == 0 ? 0 : (double)inside / total;
    }

    /// <summary>Tìm vùng có độ phủ cao nhất cho 1 khung xe, chỉ trả về nếu đạt ngưỡng "phần lớn".
    /// Trả về null nếu không vùng nào đạt ngưỡng (xe không thuộc ô đỗ nào đã định nghĩa).</summary>
    public static (DetectionZone Zone, double Coverage)? FindBestZone(
        BoundingBox box, IReadOnlyList<DetectionZone> zones, int frameWidth, int frameHeight,
        double majorityThreshold = DefaultMajorityThreshold)
    {
        (DetectionZone Zone, double Coverage)? best = null;
        foreach (var zone in zones)
        {
            var ratio = ComputeCoverageRatio(box, zone, frameWidth, frameHeight);
            if (ratio >= majorityThreshold && (best is null || ratio > best.Value.Coverage))
                best = (zone, ratio);
        }
        return best;
    }

    /// <summary>1 xe được gán cho 1 vùng: <see cref="VehicleIndex"/> là chỉ số trong danh sách khung xe truyền vào.</summary>
    public readonly record struct ZoneAssignment(int VehicleIndex, DetectionZone Zone, double Coverage);

    /// <summary>
    /// Gán xe vào vùng theo quy tắc <b>1 vùng tối đa 1 xe, 1 xe tối đa 1 vùng</b> — nên số xe được nhận
    /// KHÔNG BAO GIỜ vượt quá số vùng. Với mỗi cặp (xe, vùng) mà xe nằm trong vùng từ <paramref name="majorityThreshold"/>
    /// trở lên, điểm = <b>diện tích xe nằm trong vùng</b> (độ phủ × diện tích khung xe, tính bằng pixel). Cặp điểm cao nhất
    /// được chọn trước, rồi loại cả xe lẫn vùng đó khỏi các cặp còn lại (tham lam). Vì vậy:
    ///   • xe lớn/gần, chiếm phần lớn vùng thắng xe nhỏ/xa/bị che cùng lọt trong vùng;
    ///   • xe chỉ có 1 phần nhỏ trong vùng (độ phủ dưới ngưỡng) không bao giờ được xét;
    ///   • các xe còn lại không được gán (xe ngoài vùng hoặc vùng đã có xe khác).
    /// </summary>
    public static List<ZoneAssignment> AssignVehiclesToZones(
        IReadOnlyList<BoundingBox> boxes, IReadOnlyList<DetectionZone> zones, int frameWidth, int frameHeight,
        double majorityThreshold = DefaultMajorityThreshold)
    {
        var pairs = new List<(double Score, double Coverage, int Vehicle, int Zone)>();
        for (int i = 0; i < boxes.Count; i++)
        {
            double area = Math.Max(0, boxes[i].X2 - boxes[i].X1) * Math.Max(0, boxes[i].Y2 - boxes[i].Y1);
            for (int z = 0; z < zones.Count; z++)
            {
                var coverage = ComputeCoverageRatio(boxes[i], zones[z], frameWidth, frameHeight);
                if (coverage >= majorityThreshold)
                    pairs.Add((coverage * area, coverage, i, z));
            }
        }

        var usedVehicles = new HashSet<int>();
        var usedZones = new HashSet<int>();
        var result = new List<ZoneAssignment>();

        // Sap xep on dinh + tie-break xac dinh (diem -> do phu -> chi so) de ket qua khong doi giua cac lan chay
        foreach (var pr in pairs
                     .OrderByDescending(x => x.Score).ThenByDescending(x => x.Coverage)
                     .ThenBy(x => x.Vehicle).ThenBy(x => x.Zone))
        {
            if (usedVehicles.Contains(pr.Vehicle) || usedZones.Contains(pr.Zone)) continue;
            usedVehicles.Add(pr.Vehicle);
            usedZones.Add(pr.Zone);
            result.Add(new ZoneAssignment(pr.Vehicle, zones[pr.Zone], pr.Coverage));
        }
        return result;
    }

    /// <summary>Ray-casting chuẩn: đếm số lần tia ngang từ điểm cắt qua các cạnh đa giác.</summary>
    private static bool PointInPolygon(double x, double y, (double X, double Y)[] poly)
    {
        bool inside = false;
        int j = poly.Length - 1;
        for (int i = 0; i < poly.Length; j = i++)
        {
            var (xi, yi) = poly[i];
            var (xj, yj) = poly[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi)
                inside = !inside;
        }
        return inside;
    }
}
