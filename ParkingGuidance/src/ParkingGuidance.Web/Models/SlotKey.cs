using System.Text.RegularExpressions;

namespace ParkingGuidance.Web.Models;

/// <summary>
/// Quy ước phân cấp và khoá ô đỗ:  <b>Tầng hầm → Zone → Vị trí nhận diện</b>.
///  • Mỗi camera thuộc 1 TẦNG HẦM và 1 ZONE (khu vực lắp đặt) trong tầng đó. Nhiều camera có thể cùng 1 Zone.
///  • SỐ THỨ TỰ ZONE: số tự nhiên nguyên dương (1, 2, 3, ...) — mỗi tầng có các Zone riêng, tầng khác nhau được dùng lại cùng số.
///  • SỐ THỨ TỰ VỊ TRÍ NHẬN DIỆN (vùng vẽ trên ảnh camera, người dùng nhập): số tự nhiên nguyên dương, <b>không trùng trong cùng 1 Zone</b>
///    (kể cả giữa các camera cùng Zone); Zone khác nhau hoặc tầng khác nhau được dùng lại cùng số.
///    Chữ hay ký hiệu (vd "B1", "A-2", "1.5", "-3", "0") đều không hợp lệ. Số có số 0 đứng đầu ("007") được chuẩn hoá thành "7".
///  • KHOÁ NỘI BỘ của ô đỗ = "{TẦNG}-{ZONE}-{SỐ}" (vd "B1-2-5" = tầng B1, Zone 2, vị trí 5) để các tầng/Zone không đè lên nhau;
///    khoá này dùng ở ParkingService, phiên đỗ, bản đồ tuỳ chỉnh.
///  • Khoá kiểu cũ "{TẦNG}-{SỐ}" (vd "B1-5") được chuyển sang Zone mặc định <see cref="DefaultZone"/> khi tải dữ liệu cũ.
/// </summary>
public static class SlotKey
{
    /// <summary>Số chữ số tối đa của số thứ tự (999999).</summary>
    public const int MaxDigits = 6;

    /// <summary>Zone dùng cho dữ liệu cũ chưa có Zone (và khi 1 camera chưa gán Zone mà vẫn cần tính khoá).</summary>
    public const string DefaultZone = "1";

    /// <summary>Kiểm tra NGHIÊM NGẶT: chỉ gồm chữ số 0-9, giá trị ≥ 1. Trả về dạng chuẩn (bỏ số 0 đứng đầu).</summary>
    public static bool TryNormalizeNumber(string? raw, out string number)
    {
        number = "";
        var s = (raw ?? "").Trim();
        if (s.Length == 0 || s.Length > 12) return false;
        foreach (var c in s)
            if (c < '0' || c > '9') return false; // chi chu so ASCII (char.IsDigit nhan ca chu so cua he chu khac)

        s = s.TrimStart('0');
        if (s.Length == 0 || s.Length > MaxDigits) return false;
        number = s;
        return true;
    }

    /// <summary>Dùng để CHUYỂN DỮ LIỆU CŨ: lấy dãy chữ số ở CUỐI chuỗi ("B1-001" → "1", "B2-12" → "12", "B1-2-5" → "5", "7" → "7").</summary>
    public static bool TryExtractLegacyNumber(string? raw, out string number)
    {
        number = "";
        var m = Regex.Match(raw ?? "", @"(\d+)\s*$");
        return m.Success && TryNormalizeNumber(m.Value, out number);
    }

    public static string NormalizeFloor(string? floor) =>
        string.IsNullOrWhiteSpace(floor) ? "B1" : floor.Trim().ToUpperInvariant();

    /// <summary>Số thứ tự Zone ở dạng chuẩn; Zone trống/không hợp lệ → <see cref="DefaultZone"/>.</summary>
    public static string NormalizeZone(string? zone) =>
        TryNormalizeNumber(zone, out var z) ? z : DefaultZone;

    /// <summary>Khoá nội bộ của ô: "{TẦNG}-{ZONE}-{SỐ}". <paramref name="number"/> phải là số đã chuẩn hoá.</summary>
    public static string Make(string? floor, string? zone, string number) =>
        $"{NormalizeFloor(floor)}-{NormalizeZone(zone)}-{number}";

    /// <summary>Khoá ô của 1 vị trí nhận diện (số thứ tự <paramref name="positionNumber"/>) của camera ở tầng/Zone cho trước,
    /// hoặc null nếu số thứ tự không hợp lệ.</summary>
    public static string? ForZone(string? floor, string? zone, string? positionNumber) =>
        TryNormalizeNumber(positionNumber, out var n) ? Make(floor, zone, n) : null;

    private static readonly Regex CurrentKeyRegex = new(@"^(?<floor>.+)-(?<zone>\d+)-(?<num>\d+)$", RegexOptions.Compiled);

    /// <summary>Tách khoá dạng mới "{tầng}-{zone}-{số}". Trả về false với khoá kiểu cũ ("B1-5", "5").</summary>
    public static bool TryParse(string? key, out string floor, out string zone, out string number)
    {
        floor = zone = number = "";
        var m = CurrentKeyRegex.Match((key ?? "").Trim());
        if (!m.Success) return false;
        if (!TryNormalizeNumber(m.Groups["zone"].Value, out var z) || !TryNormalizeNumber(m.Groups["num"].Value, out var n)) return false;
        floor = m.Groups["floor"].Value; zone = z; number = n;
        return true;
    }

    /// <summary>Như <see cref="TryParse"/> nhưng biết trước tên tầng nên không nhầm khi tên tầng có dấu "-" (vd "B-1").
    /// Chỉ trả về true khi khoá đúng dạng "{TẦNG}-{zone}-{số}" của chính tầng này.</summary>
    public static bool TryParseForFloor(string? key, string? floor, out string zone, out string number)
    {
        zone = number = "";
        var prefix = NormalizeFloor(floor) + "-";
        var k = (key ?? "").Trim();
        if (!k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var m = Regex.Match(k.Substring(prefix.Length), @"^(?<zone>\d+)-(?<num>\d+)$");
        if (!m.Success) return false;
        if (!TryNormalizeNumber(m.Groups["zone"].Value, out var z) || !TryNormalizeNumber(m.Groups["num"].Value, out var n)) return false;
        zone = z; number = n;
        return true;
    }

    /// <summary>Số thứ tự vị trí lấy từ khoá ô (null nếu khoá không có phần số ở cuối).</summary>
    public static string? NumberOfKey(string? key) => TryExtractLegacyNumber(key, out var n) ? n : null;

    /// <summary>Zone của 1 ô đỗ: lấy từ khoá dạng mới nếu có, nếu không thì từ trường Zone đã lưu, cuối cùng là <see cref="DefaultZone"/>.</summary>
    public static string ZoneOf(string? slotKey, string? storedZone) =>
        TryParse(slotKey, out _, out var z, out _) ? z : NormalizeZone(storedZone);

    public const string InvalidMessage =
        "So thu tu vi tri nhan dien phai la so tu nhien nguyen duong (1, 2, 3...), khong dung chu hay ky hieu (vi du 'B1' la khong hop le).";

    public const string ZoneInvalidMessage =
        "So thu tu Zone phai la so tu nhien nguyen duong (1, 2, 3...), khong dung chu hay ky hieu.";
}
