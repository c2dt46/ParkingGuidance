using System.Text.RegularExpressions;

namespace ParkingGuidance.Web.Models;

public static class LedAreaType
{
    public const string Image = "Image";
    public const string Text = "Text";
    public const string Number = "Number";
    public static bool IsValid(string? t) => t is Image or Text or Number;
}

/// <summary>
/// 1 dòng trong bảng vùng hiển thị của 1 card (CommList của Form HDCard). Tên thuộc tính giữ NGUYÊN theo cột của PGS
/// (No, Type, OriginX..., Ttext..., Nquantity...) để người dùng PGS dễ đối chiếu.
/// </summary>
public class LedAreaRow
{
    public int No { get; set; }
    public string Type { get; set; } = LedAreaType.Number;
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public int Width { get; set; } = 32;
    public int Height { get; set; } = 16;

    // Image
    public string Ipath { get; set; } = "";
    public int Ispeed { get; set; } = 1;
    public int Istopover { get; set; }
    public int Ieffects { get; set; }

    // Text
    public string Ttext { get; set; } = "";
    public int Tspeed { get; set; } = 1;
    public int Tstopover { get; set; }
    public int Teffects { get; set; }
    public int Tcolor { get; set; }
    public int Tfont { get; set; } = 5;
    public string Tfont_Type { get; set; } = ".Vn3DH";

    // Number: giá trị hiển thị = biến MS (NMs) + biến MC (NMc) + project tag (NTag); mục để trống tính là 0.
    // (Thay cho "dữ liệu loại A/B/C" cũ — các thanh ghi A/B/C không còn trong dự án; cấu hình cũ có NAdata/NBdata/NCdata sẽ bị bỏ qua khi nạp.)
    /// <summary>Tên biến MS (MS0, MS1…); rỗng = không dùng.</summary>
    public string NMs { get; set; } = "";
    /// <summary>Tên biến MC (MC0, MC1…); rỗng = không dùng.</summary>
    public string NMc { get; set; } = "";
    /// <summary>Tên project tag kiểu số (Integer / Boolean / Real); rỗng = không dùng.</summary>
    public string NTag { get; set; } = "";
    public int Ncolor { get; set; }
    /// <summary>0 = Manual (màu cố định Ncolor), 1 = Auto (đổi màu theo giá trị).</summary>
    public int Nmode { get; set; }
    /// <summary>Số lượng ký tự (đệm 0 bên trái), 2..10.</summary>
    public int Nquantity { get; set; } = 2;
    public int NlimitY { get; set; } = 2000;
    public int NlimitR { get; set; } = 1000;
    public int Nfont { get; set; } = 5;
    public string Nfont_Type { get; set; } = ".Vn3DH";
    /// <summary>1 = hiện chữ "FULL" khi giá trị = 0.</summary>
    public int NFull { get; set; }

    public LedAreaRow Sanitized(int no)
    {
        static int C(int v, int lo, int hi) => Math.Clamp(v, lo, hi);
        static string Font(string? f) => string.IsNullOrWhiteSpace(f) ? ".Vn3DH" : f.Trim();
        static string Name(string? n) { var t = (n ?? "").Trim(); return t.Length > 64 ? t[..64] : t; }
        return new LedAreaRow
        {
            No = no,
            Type = LedAreaType.IsValid(Type) ? Type : LedAreaType.Number,
            OriginX = C(OriginX, 0, 1000), OriginY = C(OriginY, 0, 1000),
            Width = C(Width, 1, 1000), Height = C(Height, 1, 1000),
            Ipath = Ipath ?? "", Ispeed = C(Ispeed, 1, 2000), Istopover = C(Istopover, 0, 255), Ieffects = C(Ieffects, 0, HdCardCatalog.Effects.Length - 1),
            Ttext = Ttext ?? "", Tspeed = C(Tspeed, 1, 2000), Tstopover = C(Tstopover, 0, 255), Teffects = C(Teffects, 0, HdCardCatalog.Effects.Length - 1),
            Tcolor = C(Tcolor, 0, 2), Tfont = C(Tfont, 5, 139), Tfont_Type = Font(Tfont_Type),
            NMs = Name(NMs), NMc = Name(NMc), NTag = Name(NTag),
            Ncolor = C(Ncolor, 0, 2), Nmode = C(Nmode, 0, 1), Nquantity = C(Nquantity, 2, 10),
            NlimitY = C(NlimitY, 0, 99999), NlimitR = C(NlimitR, 0, 99999),
            Nfont = C(Nfont, 5, 139), Nfont_Type = Font(Nfont_Type), NFull = C(NFull, 0, 1)
        };
    }
}

/// <summary>1 card LED (Para_* của HDCard) + bảng vùng hiển thị của nó. PGS quản lý 100 card, số thứ tự 1..100.</summary>
public class LedCard
{
    private static readonly Regex IpRegex = new(@"^([1-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])(\.([0-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-5])){3}$", RegexOptions.Compiled);

    public int CardNo { get; set; }
    public string Para_IPAddress { get; set; } = "";
    public bool Para_Enable { get; set; }
    public int Para_Width { get; set; } = 32;
    public int Para_Height { get; set; } = 16;
    /// <summary>Loại card — chỉ số trong HdCardCatalog.Models.</summary>
    public int Para_Mode { get; set; }
    public int Para_Color { get; set; } = 2;
    public int Para_Gray { get; set; }
    public List<LedAreaRow> Areas { get; set; } = new();

    public static bool IsValidIp(string? ip) => !string.IsNullOrEmpty(ip) && IpRegex.IsMatch(ip);

    public static LedCard Blank(int no)
    {
        var c = new LedCard { CardNo = no };
        c.Para_Color = HdCardCatalog.DefaultColor(0);
        return c;
    }

    /// <summary>Đưa về miền hợp lệ, áp các ràng buộc của Form HDCard (bội số 32/16, giới hạn model, số khối).</summary>
    public LedCard Sanitized(int cardNo)
    {
        var mode = Math.Clamp(Para_Mode, 0, HdCardCatalog.Models.Length - 1);
        int w = Math.Clamp(Para_Width / 32 * 32, 32, HdCardCatalog.MaxWidth[mode]);
        int h = Math.Clamp(Para_Height / 16 * 16, 16, HdCardCatalog.MaxHeight[mode]);
        w = Math.Max(32, w / 32 * 32);
        h = Math.Max(16, h / 16 * 16);
        while ((h / 16) * (w / 32) > HdCardCatalog.MaxBlocks[mode] && w > 32) w -= 32; // _W_check
        while ((h / 16) * (w / 32) > HdCardCatalog.MaxBlocks[mode] && h > 16) h -= 16; // _H_check

        var colors = HdCardCatalog.ColorOptions(mode).Select(c => c.Value).ToArray();
        var rows = (Areas ?? new()).Take(HdCardCatalog.MaxAreas).Select((a, i) => a.Sanitized(i + 1)).ToList();
        return new LedCard
        {
            CardNo = cardNo,
            Para_IPAddress = IsValidIp(Para_IPAddress?.Trim()) ? Para_IPAddress!.Trim() : "",
            Para_Enable = Para_Enable,
            Para_Width = w, Para_Height = h, Para_Mode = mode,
            Para_Color = colors.Contains(Para_Color) ? Para_Color : HdCardCatalog.DefaultColor(mode),
            Para_Gray = Math.Clamp(Para_Gray, 0, HdCardCatalog.GrayCount[mode] - 1),
            Areas = rows
        };
    }

    /// <summary>Như PGS không chặn khi lưu (Hd_AddArea sẽ báo lỗi lúc gửi nếu vùng vượt màn hình); giao diện hiện cảnh báo.</summary>
    public string? Validate() => null;

    /// <summary>Dấu vân tay cấu hình (để báo "cấu hình đã đổi — cần Gửi tới card" như quy trình PGS).</summary>
    public string ConfigStamp() =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this)))[..16];
}

/// <summary>Tập khoảng số thứ tự vùng đỗ, ví dụ "1-10,12,15-20"; rỗng = tất cả. Các khoảng được gộp và tra nhị phân.</summary>
public sealed class ZoneRanges
{
    public static readonly ZoneRanges All = new(Array.Empty<(int, int)>(), true);
    private readonly (int Lo, int Hi)[] _ranges;
    public bool IsAll { get; }

    private ZoneRanges((int, int)[] ranges, bool all) { _ranges = ranges; IsAll = all; }

    public static bool TryParse(string? text, out ZoneRanges result) => TryParseMany(new[] { text }, out result);

    /// <summary>Hợp của nhiều chuỗi; chỉ cần 1 chuỗi rỗng (= cả tầng) thì kết quả là tất cả.</summary>
    public static bool TryParseMany(IEnumerable<string?> texts, out ZoneRanges result)
    {
        result = All;
        var list = new List<(int, int)>();
        bool all = false;
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) { all = true; continue; }
            if (!TryParseSingle(text, list)) return false;
        }
        if (all) return true;

        list.Sort();
        var merged = new List<(int, int)>();
        foreach (var r in list)
        {
            if (merged.Count > 0 && r.Item1 <= merged[^1].Item2 + 1)
                merged[^1] = (merged[^1].Item1, Math.Max(merged[^1].Item2, r.Item2));
            else merged.Add(r);
        }
        result = new ZoneRanges(merged.ToArray(), false);
        return true;
    }

    private static bool TryParseSingle(string text, List<(int, int)> into)
    {
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2) return false;
            if (!SlotKey.TryNormalizeNumber(bounds[0], out var a) || !int.TryParse(a, out var lo)) return false;
            var hi = lo;
            if (bounds.Length == 2 && (!SlotKey.TryNormalizeNumber(bounds[1], out var b) || !int.TryParse(b, out hi))) return false;
            if (hi < lo) (lo, hi) = (hi, lo);
            into.Add((lo, hi));
        }
        return true;
    }

    public bool Contains(int n)
    {
        if (IsAll) return true;
        int lo = 0, hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (n < _ranges[mid].Lo) hi = mid - 1;
            else if (n > _ranges[mid].Hi) lo = mid + 1;
            else return true;
        }
        return false;
    }
}

// ---------------------------------------------------------------------------
// Dữ liệu chạy (không lưu đĩa)
// ---------------------------------------------------------------------------

public enum LedState { Pending, Online, Offline, Disabled }

public sealed class LedCardStatus
{
    public int CardNo { get; set; }
    public LedState State { get; set; }
    /// <summary>Có dữ liệu mới chờ gửi.</summary>
    public bool Dirty { get; set; }
    /// <summary>Cấu hình đã đổi kể từ lần gửi cuối — cần bấm "Gửi tới card" (như quy trình PGS).</summary>
    public bool ConfigChanged { get; set; }
    public DateTime? LastSentUtc { get; set; }
    public int? LastSendMs { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public string? LastError { get; set; }
    public long SendCount { get; set; }
}
