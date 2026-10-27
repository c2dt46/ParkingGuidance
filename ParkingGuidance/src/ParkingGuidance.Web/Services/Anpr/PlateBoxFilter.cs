namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>Kết quả kiểm tra hình dạng một khung biển số.</summary>
public enum PlateShape
{
    /// <summary>Chắc chắn KHÔNG phải biển (quá nhỏ, quá dẹt/quá dài, quá to so với xe...) — bị loại.</summary>
    Invalid = 0,
    /// <summary>Tỉ lệ không điển hình (thường là khung bị CỤT một đầu hoặc quá rộng) — vẫn thử đọc nhưng xếp sau khung hợp lệ.</summary>
    Suspect = 1,
    /// <summary>Tỉ lệ đúng dạng biển số Việt Nam.</summary>
    Valid = 2,
}

/// <summary>
/// Lọc khung biển số theo HÌNH HỌC, chạy sau bộ dò YOLO (không thay bộ dò): bỏ các khung vô lý để không đẩy ảnh nền/biển hiệu vào bước đọc ký tự.
///   • Biển dài 1 dòng: rộng/cao ≈ 4,7 (ô tô 520×110) → chấp nhận 3,6–6,0.
///   • Biển vuông 2 dòng: rộng/cao ≈ 1,2–1,4 (xe máy 190×140, ô tô 2 dòng) → chấp nhận 0,85–1,75.
///   • Tỉ lệ nằm giữa (1,75–3,6) hoặc hơi ngoài: <see cref="PlateShape.Suspect"/> — thường là khung CỤT (như biển 29A-653.14 bị cắt mất "14");
///     không loại hẳn vì loại sẽ mất luôn biển, mà để bước sau thử nới rộng khung; chỉ xếp sau khung đúng dạng.
///   • Ngoài 0,6–7,5, nhỏ hơn 18×8 px, hoặc to quá 70% bề rộng / 55% chiều cao vùng tìm → loại. Khi biết xe: biển hẹp hơn 4% bề rộng xe cũng loại.
/// Hằng số để ở đây cho dễ chỉnh; tắt cả bộ lọc bằng appsettings: <c>"Anpr": { "PlateShapeFilter": false }</c>.
/// </summary>
public static class PlateBoxFilter
{
    public const double LongMin = 3.6, LongMax = 6.0;          // biển 1 dòng
    public const double SquareMin = 0.85, SquareMax = 1.75;    // biển 2 dòng (vuông)
    public const double AbsMin = 0.6, AbsMax = 7.5;            // ngoài khoảng này chắc chắn không phải biển
    public const double MinWidthPx = 18, MinHeightPx = 8;
    public const double MaxRelWidth = 0.70, MaxRelHeight = 0.55, MinRelWidthOfVehicle = 0.04;

    /// <param name="w">Rộng khung biển (px).</param>
    /// <param name="h">Cao khung biển (px).</param>
    /// <param name="refW">Rộng vùng đã tìm biển (khung xe nới ra, hoặc cả ảnh); 0 = không kiểm tra.</param>
    /// <param name="refH">Cao vùng đã tìm biển; 0 = không kiểm tra.</param>
    /// <param name="refIsVehicle">true khi vùng tham chiếu chính là khung XE → kiểm thêm biển không được quá nhỏ so với xe.</param>
    public static PlateShape Classify(double w, double h, double refW = 0, double refH = 0, bool refIsVehicle = false)
    {
        if (w < MinWidthPx || h < MinHeightPx) return PlateShape.Invalid;
        double a = w / h;
        if (a < AbsMin || a > AbsMax) return PlateShape.Invalid;
        if (refW > 0 && w > MaxRelWidth * refW) return PlateShape.Invalid;
        if (refH > 0 && h > MaxRelHeight * refH) return PlateShape.Invalid;
        if (refIsVehicle && refW > 0 && w < MinRelWidthOfVehicle * refW) return PlateShape.Invalid;
        bool typical = (a >= LongMin && a <= LongMax) || (a >= SquareMin && a <= SquareMax);
        return typical ? PlateShape.Valid : PlateShape.Suspect;
    }

    /// <summary>Loại khung <see cref="PlateShape.Invalid"/>, xếp khung Valid trước Suspect (giữ nguyên thứ tự ban đầu trong mỗi nhóm).</summary>
    public static List<T> Filter<T>(IEnumerable<T> items, Func<T, (double W, double H)> size, double refW = 0, double refH = 0, bool refIsVehicle = false)
    {
        return items
            .Select(x => { var (w, h) = size(x); return (Item: x, Shape: Classify(w, h, refW, refH, refIsVehicle)); })
            .Where(t => t.Shape != PlateShape.Invalid)
            .OrderByDescending(t => (int)t.Shape)            // OrderBy của LINQ là sắp xếp ổn định
            .Select(t => t.Item)
            .ToList();
    }
}
