using System.Text.RegularExpressions;

namespace ParkingGuidance.Web.Services.Anpr;

/// <summary>
/// Chuẩn hoá & kiểm tra kết quả OCR theo định dạng biển ô tô Việt Nam. Biển số VN có NHIỀU kiểu
/// seri khác nhau, không chỉ "1 hoặc 2 chữ cái":
///   - 1 chữ:        51L-152.17   (tỉnh 2 số + 1 chữ + 5 số)
///   - 2 chữ:        29NN-691.33  (tỉnh 2 số + 2 chữ + 5 số)
///   - Chữ + số:      30Y9-6850, 15C1-010.82  (tỉnh 2 số + chữ + SỐ + 4 hoặc 5 số — rất phổ biến
///                     khi tỉnh đã dùng hết seri thuần chữ)
///   - Serial 4 số:   29AB-0014  (biển không có dấu chấm giữa, chỉ 4 số)
///
/// Vì độ dài tổng (8 hoặc 9 ký tự) KHÔNG đủ để phân biệt các kiểu trên (vd 8 ký tự có thể là
/// "1 chữ + 5 số" HOẶC "2 ký tự seri + 4 số"), quan trọng nhất là dùng ĐỘ DÀI TỪNG DÒNG thật (với
/// biển 2 dòng, lấy trực tiếp từ kết quả phân tích dòng của Tesseract) để xác định đúng cấu trúc,
/// thay vì đoán mò theo tổng độ dài — đây chính là nguyên nhân khiến bản trước đọc sai các biển có
/// seri dạng "chữ+số" (vd ép nhầm ký tự số '1' trong seri "C1" thành chữ 'L').
///
/// Ký tự thứ 2 của seri (nếu có) được coi là "LINH HOẠT": có thể là chữ hoặc số, KHÔNG bị ép kiểu —
/// vì cả 2 khả năng đều là biển thật hợp lệ. Chỉ ép kiểu ở những vị trí chắc chắn: 2 số đầu (mã
/// tỉnh), ký tự đầu seri (luôn là chữ), và phần serial cuối (luôn là số).
/// </summary>
public static class VietnamPlateFormatter
{
    private static readonly Dictionary<char, char> ToDigit = new()
    {
        ['O'] = '0', ['Q'] = '0', ['D'] = '0', ['I'] = '1', ['L'] = '1',
        ['Z'] = '2', ['S'] = '5', ['B'] = '8', ['G'] = '6', ['T'] = '7'
    };

    private static readonly Dictionary<char, char> ToLetter = new()
    {
        ['0'] = 'D', ['1'] = 'L', ['5'] = 'S', ['8'] = 'B', ['2'] = 'Z', ['6'] = 'G',
        ['7'] = 'T', ['4'] = 'A'
    };

    private const string Digits = "0123456789";
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Cho phép biển ô tô đời cũ 4 số, seri 1 chữ (NN L NNNN). Mặc định TẮT vì hiếm gặp
    /// và dễ nhầm với biển 5 số bị OCR rơi mất 1 chữ số.</summary>
    public static bool AllowLegacyFourDigit { get; set; } = false;

    /// <summary>
    /// raw: chuỗi đã OCR được (có thể còn lẫn khoảng trắng/dấu câu, sẽ tự loại bỏ).
    /// lineLengths: độ dài (số ký tự chữ+số) của từng dòng, CHỈ truyền khi chắc chắn đây là biển
    /// 2 dòng và đã biết chính xác ranh giới 2 dòng (vd từ line_num của Tesseract) — giúp xác định
    /// đúng cấu trúc seri thay vì đoán theo tổng độ dài. Truyền null cho biển 1 dòng.
    /// </summary>
    public static (string Text, bool IsValid) Normalize(string raw, IReadOnlyList<int>? lineLengths = null)
    {
        var s = Regex.Replace(raw.ToUpperInvariant(), "[^A-Z0-9]", "");
        var candidates = new List<(string Pretty, int Fixes)>();

        if (lineLengths is { Count: 2 } && lineLengths[0] + lineLengths[1] == s.Length)
        {
            int seriesLen = lineLengths[0] - 2;
            int serialLen = lineLengths[1];
            if (seriesLen is 1 or 2 && serialLen is 4 or 5)
                TryAdd(candidates, s, seriesLen, serialLen);
        }
        else
        {
            // Bien 1 dong (hoac khong biet do dai tung dong): thu ca 2 cau truc kha di cho moi do
            // dai, chon ket qua can IT SUA NHAT (gan voi nhung gi OCR thuc su doc duoc nhat).
            int n = s.Length;
            if (n == 7 && AllowLegacyFourDigit) TryAdd(candidates, s, 1, 4);
            if (n == 8) { TryAdd(candidates, s, 1, 5); TryAdd(candidates, s, 2, 4); }
            if (n == 9) TryAdd(candidates, s, 2, 5);
        }

        if (candidates.Count == 0) return (raw, false);

        // OrderBy la sap xep ON DINH (List.Sort thi khong dam bao). Khi 2 cau truc bang so lan sua nhau
        // (vd 8 ky tu "30Y96850" doc duoc ca "30Y-968.50" lan "30Y9-6850"), uu tien cau truc DUOC THEM
        // TRUOC trong danh sach = bien 5 so (kieu hien hanh), de ket qua luon on dinh giua cac lan chay.
        return (candidates.OrderBy(c => c.Fixes).First().Pretty, true);
    }

    private static void TryAdd(List<(string, int)> candidates, string s, int seriesLen, int serialLen)
    {
        if (s.Length != 2 + seriesLen + serialLen) return;

        var chars = s.ToCharArray();
        int fixes = 0;

        // 2 so dau: luon la ma tinh -> ep kieu so
        for (int i = 0; i < 2; i++)
        {
            if (!TryForce(ref chars[i], ToDigit, Digits, ref fixes)) return;
        }
        // Ky tu dau cua seri: luon la chu
        if (!TryForce(ref chars[2], ToLetter, Letters, ref fixes)) return;

        // Ky tu THU 2 cua seri (neu co): LINH HOAT - khong ep kieu, chi chap nhan neu da la chu/so
        if (seriesLen == 2)
        {
            if (!char.IsLetterOrDigit(chars[3])) return;
        }

        // Phan serial cuoi: luon la so
        for (int i = 2 + seriesLen; i < chars.Length; i++)
        {
            if (!TryForce(ref chars[i], ToDigit, Digits, ref fixes)) return;
        }

        var head = new string(chars, 0, 2 + seriesLen);
        var tail = new string(chars, 2 + seriesLen, serialLen);
        var pretty = serialLen == 5 ? $"{head}-{tail[..3]}.{tail[3..]}" : $"{head}-{tail}";
        candidates.Add((pretty, fixes));
    }

    private static bool TryForce(ref char c, Dictionary<char, char> map, string targetSet, ref int fixes)
    {
        if (targetSet.IndexOf(c) >= 0) return true;
        if (map.TryGetValue(c, out var mapped)) { c = mapped; fixes++; return true; }
        return false;
    }

    // Ma tinh/thanh dang duoc dung tren bien o to VN (2 so dau). Chi dung de CHAM DIEM lan doc (xem PlateTextReader), khong de loai bien:
    // ma moi/ma thay doi theo thoi gian van co the hop le nen khong bao gio tu choi chi vi ma la.
    private static readonly HashSet<int> KnownProvinceCodes = new()
    {
        11, 12, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
        29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 43, 47, 48, 49,
        50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69,
        70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 88, 89,
        90, 92, 93, 94, 95, 97, 98, 99
    };

    /// <summary>2 so dau cua bien (da chuan hoa, vd "51L-152.17") co phai ma tinh/thanh thuong gap khong.</summary>
    public static bool IsKnownProvinceCode(string plate) =>
        plate.Length >= 2 && int.TryParse(plate.AsSpan(0, 2), out var code) && KnownProvinceCodes.Contains(code);
}
