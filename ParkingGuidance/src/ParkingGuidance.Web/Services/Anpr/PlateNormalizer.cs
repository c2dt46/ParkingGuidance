using System.Diagnostics;
using System.Text;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// NƠI DUY NHẤT quyết định một chuỗi biển số đọc được có dùng được hay không và sửa các nhầm lẫn (vd "308-10370" → "30B-10370").
///
/// Mọi bộ đọc đều đưa từng kết quả qua <c>Apply</c> / <c>Normalize</c> TRƯỚC khi bỏ phiếu, kèm các căn cứ lấy từ ảnh biển:
///   • <see cref="PlateColor"/> — MÀU NỀN (trắng / vàng / xanh / đỏ) do <see cref="PlateColorAnalyzer"/> tính;
///   • <see cref="PlateKind"/> — LOẠI BIỂN dự kiến (Dân sự, Kinh doanh vận tải, Cơ quan nhà nước, Quân đội, Ngoại giao...); không truyền thì suy từ màu;
///   • <c>charScores</c> — MẢNG độ tin cậy TỪNG KÝ TỰ của chuỗi đọc được (cùng thứ tự ký tự trong <c>raw</c>); null nếu bộ đọc không có
///     (hiện chỉ bộ đọc YOLO ký tự có). Có mảng này, nếu chuỗi KHÔNG khớp cú pháp nào mà bỏ đúng 1 ký tự điểm thấp nhất thì khớp, Normalize trả về chuỗi đã bỏ ký tự đó
///     và báo chỉ số ký tự đã bỏ qua <c>droppedIndexes</c>.
/// Kết quả: trả về chuỗi biển → được tính phiếu; trả về null / "" → bị loại.
///
/// Muốn tự tính cú pháp / tự loại ký tự yếu: sửa ngay trong <see cref="Normalize(string, IReadOnlyList{int}?, PlateColor, PlateKind, IReadOnlyList{float}?, out IReadOnlyList{int})"/>.
/// </summary>
public static class PlateNormalizer
{
    /// <summary>Chỉ bỏ ký tự yếu nhất để sửa cú pháp khi độ tin cậy của nó ≤ ngưỡng này (Anpr:RepairMaxScore; mặc định 1,0 = không giới hạn).</summary>
    public static volatile float PruneMaxScore = 1.0f;

    /// <param name="raw">Chuỗi đọc được, CHƯA kiểm tra (có thể lẫn khoảng trắng, dấu).</param>
    /// <param name="lineLengths">Số ký tự mỗi dòng nếu chắc chắn là biển 2 dòng (vd {3, 5}); null nếu 1 dòng hoặc không biết.</param>
    /// <param name="color">Màu nền của khung biển chứa chuỗi này.</param>
    /// <param name="plateType">Loại biển dự kiến: Civilian (dân sự), Business (kinh doanh vận tải), StateAgency (cơ quan nhà nước), Military (quân đội), Diplomatic / Foreign...</param>
    /// <param name="charScores">MẢNG độ tin cậy (0–1) của từng ký tự trong <paramref name="raw"/>, cùng thứ tự; null nếu không có. Độ dài phải bằng raw.Length
    /// (hoặc bằng số ký tự chữ + số của raw), nếu không sẽ bị bỏ qua.</param>
    /// <param name="droppedIndexes">Chỉ số (trong chuỗi đã làm sạch: chỉ chữ + số) của các ký tự đã bị bỏ để chuỗi khớp cú pháp; rỗng nếu không bỏ ký tự nào.</param>
    public static string? Normalize(string raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType,
                                    IReadOnlyList<float>? charScores, out IReadOnlyList<int> droppedIndexes)
    {


        
        droppedIndexes = Array.Empty<int>();
        // Làm sạch chuỗi, đồng thời giữ mảng độ tin cậy THẲNG HÀNG với từng ký tự còn lại
        string cleaned;
        List<float>? scores = null;
        if (charScores is not null && charScores.Count == raw.Length)
        {
            var sb = new StringBuilder(raw.Length);
            scores = new List<float>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                var k = PlateSyntax.Clean(raw[i].ToString());
                if (k.Length != 1) continue;
                sb.Append(k);
                scores.Add(charScores[i]);
            }
            cleaned = sb.ToString();
        }
        else
        {
           
   
            cleaned = PlateSyntax.Clean(raw);
            if (charScores is not null && charScores.Count == cleaned.Length) scores = charScores.ToList();
        }
        Console.WriteLine(raw);
        if (cleaned.Length < 3) return null;                       // dưới 3 ký tự: không phải biển
     
        var c = PlateSyntax.Classify(cleaned, lineLengths, color, plateType);
         
        // Không khớp cú pháp nào + có độ tin cậy từng ký tự: thử bỏ ĐÚNG 1 ký tự điểm thấp nhất (thường là ký tự thừa: tem cờ, vết bẩn, ốc vít...)
        if (c.Kind == PlateKind.Unknown && scores is not null && cleaned.Length is >= 7 and <= 11)
        {
            int weakest = 0;
            for (int i = 1; i < scores.Count; i++) if (scores[i] < scores[weakest]) weakest = i;

            if (scores[weakest] <= PruneMaxScore)
            {
                var pruned = cleaned.Remove(weakest, 1);
                var c2 = PlateSyntax.Classify(pruned, DropFromLineLengths(lineLengths, weakest), color, plateType);
                if (c2.Kind != PlateKind.Unknown)
                {
                    c = c2;
                    droppedIndexes = new[] { weakest };
                }
            }
        }
        
        if (c.Kind == PlateKind.Unknown && !PlateSyntax.AcceptUnknown) return null;   // chế độ nghiêm ngặt
        return c.Pretty;
    }

    /// <summary>Số ký tự mỗi dòng sau khi bỏ ký tự ở vị trí <paramref name="index"/> (tính từ đầu chuỗi, theo thứ tự dòng).</summary>
    private static int[]? DropFromLineLengths(IReadOnlyList<int>? lineLengths, int index)
    {
        if (lineLengths is null) return null;
        var result = lineLengths.ToArray();
        int start = 0;
        for (int r = 0; r < result.Length; r++)
        {
            if (index < start + result[r]) { result[r]--; return result; }
            start += result[r];
        }
        return null;     // chỉ số nằm ngoài các dòng: không còn biết cấu trúc dòng
    }

    public static string? Normalize(string raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType, IReadOnlyList<float>? charScores) =>
        Normalize(raw, lineLengths, color, plateType, charScores, out _);

    public static string? Normalize(string raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType) =>
        Normalize(raw, lineLengths, color, plateType, null, out _);

    public static string? Normalize(string raw, IReadOnlyList<int>? lineLengths, PlateColor color) =>
        Normalize(raw, lineLengths, color, PlateSyntax.ExpectedKind(color), null, out _);

    public static string? Normalize(string raw, IReadOnlyList<int>? lineLengths) =>
        Normalize(raw, lineLengths, PlateColor.Unknown, PlateKind.Unknown, null, out _);

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

    /// <summary>Gọi <c>Normalize</c> an toàn. Lỗi → dùng bộ kiểm tra dân sự cũ để hệ thống không ngừng nhận diện.</summary>
    public static string? Apply(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType,
                                IReadOnlyList<float>? charScores, out IReadOnlyList<int> droppedIndexes)
    {
        droppedIndexes = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var result = Normalize(raw, lineLengths, color, plateType, charScores, out droppedIndexes);
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("PlateNormalizer.Normalize loi: " + ex.Message);
            droppedIndexes = Array.Empty<int>();
            var (text, valid) = VietnamPlateFormatter.Normalize(raw, lineLengths);
            return valid ? text : null;
        }
    }

    public static string? Apply(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType) =>
        Apply(raw, lineLengths, color, plateType, null, out _);

    public static string? Apply(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color) =>
        Apply(raw, lineLengths, color, PlateSyntax.ExpectedKind(color), null, out _);

    public static string? Apply(string? raw, IReadOnlyList<int>? lineLengths = null) =>
        Apply(raw, lineLengths, PlateColor.Unknown, PlateKind.Unknown, null, out _);
}
