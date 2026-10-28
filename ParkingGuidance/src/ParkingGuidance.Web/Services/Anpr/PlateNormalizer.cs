namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// NƠI DUY NHẤT quyết định một chuỗi biển số đọc được có dùng được hay không và sửa các nhầm lẫn (vd "308-10370" → "30B-10370").
///
/// Mọi bộ đọc (model OCR, bộ phân loại ký tự, YOLO ký tự, Tesseract) đều đưa từng kết quả qua <see cref="Apply(string?, IReadOnlyList{int}?, PlateColor)"/>
/// TRƯỚC khi bỏ phiếu, kèm hai căn cứ lấy từ ảnh biển:
///   • <see cref="PlateColor"/> — MÀU NỀN (trắng / vàng / xanh / đỏ) do <see cref="PlateColorAnalyzer"/> tính;
///   • <see cref="PlateKind"/> — LOẠI BIỂN dự kiến (Dân sự, Kinh doanh vận tải, Cơ quan nhà nước, Quân đội, Ngoại giao...); nếu không truyền thì suy từ màu
///     bằng <see cref="PlateSyntax.ExpectedKind"/>.
/// Kết quả: trả về chuỗi biển → được tính phiếu; trả về null / "" → bị loại.
///
/// Quy tắc cú pháp theo loại biển nằm ở <see cref="PlateSyntax.Classify(string?, IReadOnlyList{int}?, PlateColor, PlateKind)"/>; muốn tự tính cú pháp thì sửa
/// ngay trong <see cref="Normalize(string, IReadOnlyList{int}?, PlateColor, PlateKind)"/> — cả <c>color</c> lẫn <c>plateType</c> luôn được truyền tường minh.
/// <see cref="PlateColor.Unknown"/> / <see cref="PlateKind.Unknown"/> = chưa biết → cú pháp tổng quát.
/// </summary>
public static class PlateNormalizer
{
    /// <param name="raw">Chuỗi đọc được, CHƯA kiểm tra (có thể lẫn khoảng trắng, dấu).</param>
    /// <param name="lineLengths">Số ký tự mỗi dòng nếu chắc chắn là biển 2 dòng (vd {3, 5}); null nếu 1 dòng hoặc không biết.</param>
    /// <param name="color">Màu nền của khung biển chứa chuỗi này.</param>
    /// <param name="plateType">Loại biển dự kiến: Civilian (dân sự), Business (kinh doanh vận tải), StateAgency (cơ quan nhà nước), Military (quân đội), Diplomatic / Foreign...</param>
    public static string? Normalize(string raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType)
    {
  //      if (plateType == PlateKind.Business)

        var cleaned = PlateSyntax.Clean(raw);
        if (cleaned.Length < 3) return null;                       // dưới 3 ký tự: không phải biển
        var c = PlateSyntax.Classify(cleaned, lineLengths, color, plateType);
        if (c.Kind == PlateKind.Unknown && !PlateSyntax.AcceptUnknown) return null;   // chế độ nghiêm ngặt
        return c.Pretty;
    }

    public static string? Normalize(string raw, IReadOnlyList<int>? lineLengths, PlateColor color) =>
        Normalize(raw, lineLengths, color, PlateSyntax.ExpectedKind(color));

    public static string? Normalize(string raw, IReadOnlyList<int>? lineLengths) =>
        Normalize(raw, lineLengths, PlateColor.Unknown, PlateKind.Unknown);

    /// <summary>Phân loại đầy đủ (loại biển, chuỗi đẹp, độ tin cậy cấu trúc, tên loại biển) của một chuỗi biển.</summary>
    public static PlateClass Classify(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType) =>
        PlateSyntax.Classify(raw, lineLengths, color, plateType);

    public static PlateClass Classify(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color) =>
        PlateSyntax.Classify(raw, lineLengths, color);

    /// <summary>
    /// Trọng số khi bỏ phiếu (nhân với độ tin cậy của lần đọc): khớp đúng cú pháp của loại biển dự kiến 1,0; dân sự mã tỉnh lạ 0,4;
    /// quân đội / nước ngoài khi chưa biết loại 0,85; khác loại dự kiến ×0,6; không khớp cú pháp nào 0,3.
    /// </summary>
    public static double Weight(string plate, PlateColor color, PlateKind plateType) => PlateSyntax.TrustOf(plate, color, plateType);

    public static double Weight(string plate, PlateColor color) => PlateSyntax.TrustOf(plate, color);

    public static double Weight(string plate) => Weight(plate, PlateColor.Unknown);

    /// <summary>Gọi <see cref="Normalize(string, IReadOnlyList{int}?, PlateColor, PlateKind)"/> an toàn. Lỗi → dùng bộ kiểm tra dân sự cũ để hệ thống không ngừng nhận diện.</summary>
    public static string? Apply(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var result = Normalize(raw, lineLengths, color, plateType);
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PlateNormalizer.Normalize loi: " + ex.Message);
            var (text, valid) = VietnamPlateFormatter.Normalize(raw, lineLengths);
            return valid ? text : null;
        }
    }

    public static string? Apply(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color) =>
        Apply(raw, lineLengths, color, PlateSyntax.ExpectedKind(color));

    public static string? Apply(string? raw, IReadOnlyList<int>? lineLengths = null) =>
        Apply(raw, lineLengths, PlateColor.Unknown, PlateKind.Unknown);
}
