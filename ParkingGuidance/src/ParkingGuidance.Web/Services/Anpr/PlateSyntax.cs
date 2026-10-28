using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>Bộ đọc đã đưa ra phiếu (cờ bit). Dùng để biết một kết quả có ≥ 2 ý kiến ĐỘC LẬP đồng ý hay chưa.</summary>
[Flags]
public enum ReaderSource
{
    None = 0,
    Yolo = 1,
    Cnn = 2,
    Crnn = 4,
    Tesseract = 8
}

/// <summary>Loại biển theo CẤU TRÚC ký tự (không phải theo màu nền biển).</summary>
public enum PlateKind
{
    /// <summary>Không khớp cấu trúc nào đã biết (biển đặc biệt chưa có quy tắc, hoặc đọc sai nhiều ký tự).</summary>
    Unknown = 0,
    /// <summary>Biển dân sự trong nước (ô tô / xe máy): NN L(L|N) NNN.NN ... — xem <see cref="VietnamPlateFormatter"/>.</summary>
    Civilian = 1,
    /// <summary>Biển quân đội: 2 chữ + 4–5 số (vd AA-1234).</summary>
    Military = 2,
    /// <summary>Biển tổ chức / cá nhân nước ngoài (sêri NN): mã địa phương + mã nước (3 số) + sêri + số.</summary>
    Foreign = 3,
    /// <summary>Khớp một quy tắc tuỳ chỉnh trong appsettings (Anpr:PlateRules), vd biển cảnh sát giao thông.</summary>
    Special = 4,
    /// <summary>Biển xanh của cơ quan Đảng / Nhà nước / đơn vị sự nghiệp: cú pháp dân sự + sêri nằm trong danh sách biển xanh (Anpr:BlueSeries).</summary>
    StateAgency = 5,
    /// <summary>Biển nền vàng: xe kinh doanh vận tải (cùng cú pháp biển dân sự).</summary>
    Business = 6,
    /// <summary>Biển ngoại giao / tổ chức quốc tế / công vụ nước ngoài (sêri NG, QT, CV).</summary>
    Diplomatic = 7
}

/// <param name="Kind">Loại biển nhận ra.</param>
/// <param name="Pretty">Chuỗi đã sửa lỗi theo vị trí và định dạng đẹp (nếu <see cref="PlateKind.Unknown"/> thì chỉ là chữ + số viết hoa).</param>
/// <param name="KnownProvince">Biển dân sự có mã tỉnh nằm trong danh sách thường gặp.</param>
/// <param name="Trust">Hệ số tin cậy cấu trúc (0..1) — nhân vào trọng số phiếu bầu.</param>
/// <param name="Label">Tên LOẠI BIỂN để hiển thị (tiếng Việt); biển theo quy tắc tuỳ chỉnh thì là tên quy tắc.</param>
public readonly record struct PlateClass(PlateKind Kind, string Pretty, bool KnownProvince, double Trust, string Label = "");

/// <summary>
/// Phân loại một chuỗi biển số thành NHIỀU lớp cấu trúc thay vì chỉ "hợp lệ / không hợp lệ".
///
/// Vì sao cần: <see cref="VietnamPlateFormatter"/> chỉ biết biển dân sự. Biển quân đội, ngoại giao... không theo cú pháp đó nên bị báo "không hợp lệ",
/// nhưng ta KHÔNG muốn loại chúng. Giải pháp: mỗi loại biển có quy tắc riêng + một hệ số tin cậy. Chuỗi không khớp loại nào vẫn được giữ
/// (<see cref="AcceptUnknown"/> = true) nhưng chỉ có <see cref="TrustUnknown"/> ≈ 0,3 nên chỉ thắng khi không còn lựa chọn nào có cấu trúc.
///
/// Muốn thêm loại biển mới: viết thêm 1 hàm TryXxx như <see cref="TryMilitary"/> rồi gọi trong <see cref="Classify"/>.
/// </summary>
public static class PlateSyntax
{
    public const double TrustCivilianKnown = 1.0;
    public const double TrustCivilianOddProvince = 0.4;   // 2 số đầu không phải mã tỉnh thường gặp (thường là đọc sai 1–2 ký tự đầu)
    public const double TrustSpecial = 0.85;              // quân đội / nước ngoài: đúng cấu trúc nhưng chưa có danh sách ký hiệu để kiểm chứng
    public const double TrustUnknown = 0.3;               // không khớp cấu trúc nào
    public const double TrustUnknownBadLength = 0.15;     // không khớp và độ dài lạ (&lt; 5 hoặc &gt; 10 ký tự)

    /// <summary>true (mặc định) = giữ cả chuỗi không khớp cấu trúc nào (trọng số thấp). false = chế độ nghiêm ngặt, loại hẳn.
    /// Đặt từ appsettings: Anpr:AcceptUnknownPlates.</summary>
    public static volatile bool AcceptUnknown = true;

    private static readonly string[] ForeignSeries = { "NG", "QT", "CV", "NN" };

    private static readonly Dictionary<char, char> ToDigit = new()
    {
        ['O'] = '0', ['Q'] = '0', ['D'] = '0', ['I'] = '1', ['L'] = '1',
        ['Z'] = '2', ['S'] = '5', ['B'] = '8', ['G'] = '6', ['T'] = '7'
    };

    /// <summary>Chỉ giữ chữ + số, viết hoa, Đ → D.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.ToUpperInvariant())
        {
            var c = ch == 'Đ' ? 'D' : ch;
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Phân loại KHÔNG phụ thuộc màu biển (dùng khi chưa biết màu hoặc màu không khớp quy tắc nào).</summary>
    private static PlateClass ClassifyGeneric(string s, IReadOnlyList<int>? lineLengths)
    {
        // 1) Dân sự (ô tô / xe máy trong nước)
        var (text, valid) = VietnamPlateFormatter.Normalize(s, lineLengths);
        if (valid)
        {
            bool known = VietnamPlateFormatter.IsKnownProvinceCode(text);
            return new PlateClass(PlateKind.Civilian, text, known, known ? TrustCivilianKnown : TrustCivilianOddProvince);
        }

        // 2) Quân đội
        if (TryMilitary(s, lineLengths, out var mil))
            return new PlateClass(PlateKind.Military, mil, true, TrustSpecial);

        // 3) Cơ quan / tổ chức / cá nhân nước ngoài (NG, QT, CV, NN)
        if (TryForeign(s, out var foreign, out var foreignSeries))
            return new PlateClass(foreignSeries == "NN" ? PlateKind.Foreign : PlateKind.Diplomatic, foreign, true, TrustSpecial);

        // 4) Không rõ: vẫn giữ nhưng trọng số thấp
        return new PlateClass(PlateKind.Unknown, s, false, (s.Length is >= 5 and <= 10) ? TrustUnknown : TrustUnknownBadLength);
    }

    // ====== Quy tắc theo MÀU NỀN biển ======

    // ====== Màu nền được TRUYỀN TƯỜNG MINH qua tham số (không dùng biến ngầm) ======

    public const double TrustColorMatched = 1.0;     // chuỗi đúng cú pháp của đúng màu biển: tin tối đa
    public const double TrustColorMismatch = 0.6;    // đúng cú pháp nhưng KHÁC loại màu (màu có thể bị ánh sáng làm sai): giảm tin, không loại

    private sealed record CompiledRule(string Name, PlateColor Color, Regex Rx, double Trust);
    private static volatile CompiledRule[] _rules = Array.Empty<CompiledRule>();
    private static volatile string _blueSeries = "ABCDEFGHKLM";    // Thông tư 79/2024/TT-BCA: sêri biển xanh ô tô; đổi bằng Anpr:BlueSeries

    /// <summary>
    /// Đọc cấu hình: <c>Anpr:BlueSeries</c> (chuỗi chữ cái sêri biển xanh) và <c>Anpr:PlateRules</c> — danh sách quy tắc riêng, mỗi quy tắc gồm
    /// <c>Name</c>, <c>Color</c> (White/Yellow/Blue/Red, bỏ trống = mọi màu), <c>Pattern</c> (regex khớp TOÀN BỘ chuỗi chỉ gồm chữ + số viết hoa,
    /// vd <c>^80[A-Z]\d{5}$</c>), <c>Trust</c> (0..1, mặc định 0,95). Nhóm trong regex (nếu có) được nối bằng '-' làm chuỗi hiển thị.
    /// Dùng cho biển cảnh sát giao thông và các biển đặc biệt khác mà cú pháp chung không bao phủ.
    /// </summary>
    public static void Configure(IConfiguration config)
    {
        var bs = Clean(config["Anpr:BlueSeries"]);
        if (bs.Length > 0) _blueSeries = bs;

        var list = new List<CompiledRule>();
        foreach (var c in config.GetSection("Anpr:PlateRules").GetChildren())
        {
            var pattern = c["Pattern"];
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                var color = Enum.TryParse<PlateColor>(c["Color"], true, out var pc) ? pc : PlateColor.Unknown;
                double trust = double.TryParse(c["Trust"], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0.95;
                list.Add(new CompiledRule(c["Name"] ?? "Rule", color,
                    new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
                    Math.Clamp(trust, 0.0, 1.0)));
            }
            catch (ArgumentException)
            {
                // regex sai cú pháp → bỏ qua quy tắc này
            }
        }
        _rules = list.ToArray();
    }

    /// <summary>Phân loại khi CHƯA biết màu nền (cú pháp tổng quát).</summary>
    public static PlateClass Classify(string? raw, IReadOnlyList<int>? lineLengths = null) =>
        Classify(raw, lineLengths, PlateColor.Unknown);

    /// <summary>
    /// Phân loại có xét màu nền: (0) quy tắc tuỳ chỉnh hợp màu (vd CSGT); rồi theo màu —
    /// ĐỎ: biển quân đội; XANH: cú pháp dân sự + sêri biển xanh; TRẮNG: cú pháp dân sự hoặc biển nước ngoài (NG/QT/CV/NN); VÀNG: cú pháp dân sự.
    /// Khớp → <see cref="TrustColorMatched"/>. Không khớp quy tắc của màu → giữ kết quả tổng quát nhưng nhân <see cref="TrustColorMismatch"/>
    /// (màu đọc từ ảnh có thể sai khi thiếu sáng). Chưa biết màu → hành vi tổng quát như trước.
    /// </summary>
    public static PlateClass Classify(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color) =>
        Classify(raw, lineLengths, color, ExpectedKind(color));

    /// <summary>
    /// Phân loại khi đã biết cả MÀU NỀN và LOẠI BIỂN dự kiến (<paramref name="plateType"/>: Dân sự, Kinh doanh vận tải, Cơ quan nhà nước, Quân đội, Ngoại giao...).
    /// Loại biển quyết định bộ cú pháp được kiểm: <see cref="PlateKind.Civilian"/> = dân sự (hoặc nước ngoài / ngoại giao, vì cũng nền trắng),
    /// <see cref="PlateKind.Business"/> = dân sự đánh dấu kinh doanh, <see cref="PlateKind.StateAgency"/> = dân sự + sêri biển xanh,
    /// <see cref="PlateKind.Military"/> = quân đội, <see cref="PlateKind.Diplomatic"/> / <see cref="PlateKind.Foreign"/> = NG, QT, CV / NN,
    /// <see cref="PlateKind.Unknown"/> = chưa biết → cú pháp tổng quát. Màu vẫn dùng để lọc quy tắc tuỳ chỉnh (<c>Anpr:PlateRules</c>).
    /// </summary>
    public static PlateClass Classify(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType)
    {
        var c = ClassifyCore(raw, lineLengths, color, plateType);
        return c.Label.Length > 0 ? c : c with { Label = LabelOf(c.Kind) };
    }

    /// <summary>Loại biển DỰ KIẾN theo màu nền (trước khi đọc ký tự): trắng → dân sự, vàng → kinh doanh vận tải, xanh → cơ quan nhà nước, đỏ → quân đội.</summary>
    public static PlateKind ExpectedKind(PlateColor color) => color switch
    {
        PlateColor.White => PlateKind.Civilian,
        PlateColor.Yellow => PlateKind.Business,
        PlateColor.Blue => PlateKind.StateAgency,
        PlateColor.Red => PlateKind.Military,
        _ => PlateKind.Unknown
    };

    /// <summary>Tên loại biển (tiếng Việt) theo <see cref="PlateKind"/>.</summary>
    public static string LabelOf(PlateKind kind) => kind switch
    {
        PlateKind.Civilian => "Biển dân sự (cá nhân / doanh nghiệp)",
        PlateKind.Business => "Biển kinh doanh vận tải (nền vàng)",
        PlateKind.StateAgency => "Biển cơ quan Đảng, Nhà nước (nền xanh)",
        PlateKind.Military => "Biển quân đội (nền đỏ)",
        PlateKind.Diplomatic => "Biển ngoại giao / tổ chức quốc tế / công vụ nước ngoài",
        PlateKind.Foreign => "Biển tổ chức, cá nhân nước ngoài (NN)",
        PlateKind.Special => "Biển đặc biệt (quy tắc riêng)",
        _ => "Không khớp cú pháp nào"
    };

    private static PlateClass ClassifyCore(string? raw, IReadOnlyList<int>? lineLengths, PlateColor color, PlateKind plateType)
    {
        var s = Clean(raw);
        if (s.Length == 0) return new PlateClass(PlateKind.Unknown, "", false, 0.0);

        foreach (var rule in _rules)
        {
            if (rule.Color != PlateColor.Unknown && color != PlateColor.Unknown && rule.Color != color) continue;
            var m = rule.Rx.Match(s);
            if (m.Success && m.Index == 0 && m.Length == s.Length)
                return new PlateClass(PlateKind.Special, FormatRule(m, s), true, rule.Trust, "Biển đặc biệt: " + rule.Name);
        }

        var g = ClassifyGeneric(s, lineLengths);
        if (plateType == PlateKind.Unknown) return g;

        switch (plateType)
        {
            case PlateKind.Military:
                if (TryMilitary(s, lineLengths, out var mil))
                    return new PlateClass(PlateKind.Military, mil, true, TrustColorMatched);
                break;
            case PlateKind.StateAgency:
                if (g.Kind == PlateKind.Civilian && BlueSeriesOk(g.Pretty))
                    return g with { Kind = PlateKind.StateAgency, Trust = g.KnownProvince ? TrustColorMatched : g.Trust };
                break;
            case PlateKind.Civilian:      // nền trắng: dân sự, hoặc biển nước ngoài / ngoại giao (cũng nền trắng)
                if (g.Kind is PlateKind.Civilian or PlateKind.Foreign or PlateKind.Diplomatic) return g;
                break;
            case PlateKind.Business:      // nền vàng: cú pháp dân sự, đánh dấu kinh doanh vận tải
                if (g.Kind == PlateKind.Civilian) return g with { Kind = PlateKind.Business };
                break;
            case PlateKind.Diplomatic:
            case PlateKind.Foreign:
                if (g.Kind is PlateKind.Foreign or PlateKind.Diplomatic) return g;
                break;
        }

        // Không khớp loại biển dự kiến: giữ kết quả tổng quát nhưng giảm tin (màu / loại biển dự kiến có thể sai do ánh sáng)
        return g.Kind == PlateKind.Unknown ? g : g with { Trust = g.Trust * TrustColorMismatch };
    }

    private static bool BlueSeriesOk(string pretty)
    {
        var key = Clean(pretty);
        return key.Length > 2 && char.IsAsciiLetter(key[2]) && _blueSeries.Contains(key[2]);
    }

    private static string FormatRule(Match m, string s)
    {
        var groups = m.Groups.Cast<Group>().Skip(1).Where(g => g.Success && g.Length > 0).Select(g => g.Value).ToList();
        return groups.Count > 0 ? string.Join("-", groups) : s;
    }

    /// <summary>Hệ số tin cậy cấu trúc của một chuỗi biển (đã chuẩn hoá hay chưa đều được).</summary>
    public static double TrustOf(string? plate, PlateColor color = PlateColor.Unknown) => Classify(plate, null, color).Trust;

    public static double TrustOf(string? plate, PlateColor color, PlateKind plateType) => Classify(plate, null, color, plateType).Trust;

    /// <summary>
    /// Biển quân đội: 2 chữ (ký hiệu quân khu / quân chủng / binh chủng) + 4 hoặc 5 số. Hai ký tự đầu PHẢI đã là chữ (không ép kiểu từ số),
    /// vì ép kiểu ở đây sẽ biến gần như mọi chuỗi 6–7 ký tự thành "biển quân đội". Phần số được sửa lỗi (O→0, B→8, ...).
    /// Chưa có danh sách ký hiệu hợp lệ: khi có (từ tài liệu của bạn), thêm bước kiểm tra tiền tố ở đây.
    /// </summary>
    private static bool TryMilitary(string s, IReadOnlyList<int>? lineLengths, out string pretty)
    {
        pretty = "";
        if (s.Length is < 6 or > 7) return false;
        if (!char.IsAsciiLetter(s[0]) || !char.IsAsciiLetter(s[1])) return false;
        if (lineLengths is { Count: 2 } && (lineLengths[0] != 2 || lineLengths[0] + lineLengths[1] != s.Length)) return false;

        var digits = new StringBuilder(s.Length - 2);
        for (int i = 2; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsAsciiDigit(c)) digits.Append(c);
            else if (ToDigit.TryGetValue(c, out var d)) digits.Append(d);
            else return false;
        }

        var tail = digits.ToString();
        pretty = tail.Length == 5 ? $"{s[..2]}-{tail[..3]}.{tail[3..]}" : $"{s[..2]}-{tail}";
        return true;
    }

    /// <summary>
    /// Biển nước ngoài / ngoại giao. Theo Thông tư 24/2023/TT-BCA: ký hiệu địa phương (2 số), ký hiệu nước / tổ chức quốc tế (3 số) và sêri
    /// (NG = ngoại giao, QT = tổ chức quốc tế, CV = công vụ, NN = tổ chức/cá nhân nước ngoài) + số thứ tự (2–3 số).
    /// Thứ tự các nhóm trên biển thật khác nhau giữa ô tô / xe máy nên chấp nhận cả 2 vị trí của sêri (sau mã nước, hoặc ngay sau mã địa phương).
    /// Sêri phải đã là chữ đúng; các ký tự còn lại phải là số (được sửa lỗi O→0, B→8, ...). Chuỗi hiển thị chỉ là định dạng gợi ý —
    /// khoá so khớp chỉ dùng chữ + số nên không ảnh hưởng.
    /// </summary>
    private static bool TryForeign(string s, out string pretty, out string series)
    {
        pretty = "";
        series = "";
        foreach (int pos in new[] { 5, 2 })
        {
            if (s.Length < pos + 2) continue;
            var token = s.Substring(pos, 2);
            if (Array.IndexOf(ForeignSeries, token) < 0) continue;

            var rest = s.Remove(pos, 2);
            if (rest.Length is < 7 or > 8) continue;

            var sb = new StringBuilder(rest.Length);
            bool ok = true;
            foreach (var c in rest)
            {
                if (char.IsAsciiDigit(c)) sb.Append(c);
                else if (ToDigit.TryGetValue(c, out var d)) sb.Append(d);
                else { ok = false; break; }
            }
            if (!ok) continue;

            var digits = sb.ToString();
            series = token;
            pretty = pos == 5
                ? $"{digits[..2]}-{digits.Substring(2, 3)}-{token}-{digits[5..]}"
                : $"{digits[..2]}-{token}-{digits.Substring(2, 3)}-{digits[5..]}";
            return true;
        }
        return false;
    }
}

/// <summary>Chọn kết quả cuối từ các phiếu của nhiều bộ đọc.</summary>
public static class PlateVoteResolver
{
    /// <summary>
    /// Mặc định lấy phiếu điểm cao nhất. Ngoại lệ: khi 2 bộ đọc KHÁC NHAU cho 2 chuỗi lệch nhau đúng 1 ký tự và chuỗi xếp thứ 2 hợp cấu trúc hơn
    /// hẳn (vd dân sự / quân đội so với "không rõ"), điểm không thấp hơn 60% → ưu tiên chuỗi hợp cấu trúc.
    /// </summary>
    public static PlateVote? Best(Dictionary<string, PlateVote> votes)
    {
        if (votes.Count == 0) return null;
        var ordered = votes.OrderByDescending(kv => kv.Value.Score).ToList();
        var top = ordered[0];
        if (ordered.Count == 1) return top.Value;

        double topTrust = PlateSyntax.TrustOf(top.Value.Pretty, top.Value.Color);
        foreach (var other in ordered.Skip(1).Take(3))
        {
            if (other.Key.Length != top.Key.Length || Hamming(other.Key, top.Key) != 1) continue;

            var a = other.Value.Sources;
            var b = top.Value.Sources;
            bool differentReaders = (a & ~b) != 0 || (b & ~a) != 0;
            if (!differentReaders) continue;

            if (PlateSyntax.TrustOf(other.Value.Pretty, other.Value.Color) > topTrust + 0.2 && other.Value.Score >= 0.6 * top.Value.Score)
                return other.Value;
        }
        return top.Value;
    }

    private static int Hamming(string a, string b)
    {
        int d = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) d++;
        return d;
    }
}
