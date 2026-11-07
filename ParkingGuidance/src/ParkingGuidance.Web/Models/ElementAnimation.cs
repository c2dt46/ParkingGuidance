using System.Text.RegularExpressions;

namespace ParkingGuidance.Web.Models;

/// <summary>
/// Hiệu ứng động gắn biến (kiểu SCADA) cho MỌI loại đối tượng của Layout. Chỉ có tác dụng ở chế độ Chạy:
/// <list type="bullet">
/// <item><b>Hiển thị</b>: ẩn / hiện theo giá trị của 1 biến.</item>
/// <item><b>Vị trí</b>: giá trị biến (khoảng ValueMin..ValueMax) ánh xạ tuyến tính sang vị trí ngang / dọc (PosMin..PosMax, % khung mặt bằng).
///       Có thể bật "Slider": kéo đối tượng ở chế độ Chạy để ghi ngược giá trị về biến (Project tag).</item>
/// <item><b>Màu sắc</b>: màu nền / viền / chữ đổi theo ngưỡng giá trị của 1 biến, có thể nhấp nháy.</item>
/// </list>
/// </summary>
public class ElementAnimation
{
    // --- Hiển thị ---
    /// <summary>Biến điều khiển hiển thị: khác 0 (hoặc true) = hiện.</summary>
    public string? VisibilityTag { get; set; }
    /// <summary>true = đảo: khác 0 thì ẨN.</summary>
    public bool VisibilityInvert { get; set; }

    // --- Vị trí ---
    public AxisAnimation? Horizontal { get; set; }
    public AxisAnimation? Vertical { get; set; }

    // --- Slider / Gestures ---
    /// <summary>Cho kéo đối tượng ở chế độ Chạy để ghi giá trị về biến (chỉ Project tag, cần có Horizontal hoặc Vertical).</summary>
    public bool SliderEnabled { get; set; }
    /// <summary>Biến vô hiệu hoá slider: khác 0 (hoặc true) = không cho kéo.</summary>
    public string? SliderDisableTag { get; set; }

    // --- Màu sắc ---
    public ColorAnimation? Colors { get; set; }

    private static readonly Regex TagRx = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex HexRx = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    internal static string? Tag(string? v) => !string.IsNullOrWhiteSpace(v) && TagRx.IsMatch(v.Trim()) ? v.Trim() : null;
    internal static double Num(double v, double min, double max, double def) =>
        double.IsNaN(v) || double.IsInfinity(v) ? def : Math.Clamp(v, min, max);
    internal static string? Hex(string? v) => v is not null && HexRx.IsMatch(v) ? v.ToLowerInvariant() : null;

    /// <summary>Bản đã kiểm tra (tên biến hợp lệ, số trong miền, màu #rrggbb, tối đa 8 ngưỡng). Không có hiệu ứng nào → null.</summary>
    public static ElementAnimation? Clean(ElementAnimation? a)
    {
        if (a is null) return null;
        var c = new ElementAnimation
        {
            VisibilityTag = Tag(a.VisibilityTag),
            VisibilityInvert = a.VisibilityInvert,
            Horizontal = AxisAnimation.Clean(a.Horizontal),
            Vertical = AxisAnimation.Clean(a.Vertical),
            Colors = ColorAnimation.Clean(a.Colors),
        };
        if (c.VisibilityTag is null) c.VisibilityInvert = false;
        c.SliderEnabled = a.SliderEnabled && (c.Horizontal is not null || c.Vertical is not null);
        c.SliderDisableTag = c.SliderEnabled ? Tag(a.SliderDisableTag) : null;

        bool any = c.VisibilityTag is not null || c.Horizontal is not null || c.Vertical is not null || c.Colors is not null;
        return any ? c : null;
    }
}

/// <summary>Một trục (ngang hoặc dọc): biến → vị trí. Vị trí = PosMin + (giá trị − ValueMin) / (ValueMax − ValueMin) × (PosMax − PosMin).</summary>
public class AxisAnimation
{
    public string? Tag { get; set; }
    public double ValueMin { get; set; } = 0;
    public double ValueMax { get; set; } = 100;
    /// <summary>Vị trí tương ứng ValueMin / ValueMax, tính theo % khung mặt bằng (0-100).</summary>
    public double PosMin { get; set; } = 0;
    public double PosMax { get; set; } = 100;
    /// <summary>Điểm của đối tượng đặt đúng vào vị trí: "start" (TRÁI / TRÊN) | "center" | "end" (PHẢI / DƯỚI).</summary>
    public string Reference { get; set; } = "start";

    public static AxisAnimation? Clean(AxisAnimation? a)
    {
        var tag = ElementAnimation.Tag(a?.Tag);
        if (a is null || tag is null) return null;
        var vMin = ElementAnimation.Num(a.ValueMin, -1e15, 1e15, 0);
        var vMax = ElementAnimation.Num(a.ValueMax, -1e15, 1e15, 100);
        if (vMax == vMin) vMax = vMin + 1;     // tránh chia cho 0
        return new AxisAnimation
        {
            Tag = tag,
            ValueMin = vMin,
            ValueMax = vMax,
            PosMin = ElementAnimation.Num(a.PosMin, -100, 200, 0),
            PosMax = ElementAnimation.Num(a.PosMax, -100, 200, 100),
            Reference = a.Reference is "center" or "end" ? a.Reference : "start"
        };
    }
}

/// <summary>Đổi màu theo ngưỡng ("By Limit"): giá trị ≥ ngưỡng lớn nhất thoả mãn → lấy màu của ngưỡng đó.</summary>
public class ColorAnimation
{
    public string? Tag { get; set; }
    /// <summary>Áp màu cho: nền / viền / chữ (chọn nhiều được).</summary>
    public bool Fill { get; set; } = true;
    public bool Border { get; set; }
    public bool Text { get; set; }
    public List<ColorStop> Stops { get; set; } = new();

    public static ColorAnimation? Clean(ColorAnimation? a)
    {
        var tag = ElementAnimation.Tag(a?.Tag);
        if (a is null || tag is null) return null;
        var stops = (a.Stops ?? new List<ColorStop>())
            .Where(s => s.Limit is not null && !double.IsNaN(s.Limit.Value) && !double.IsInfinity(s.Limit.Value) && ElementAnimation.Hex(s.Color) is not null)
            .Select(s => new ColorStop
            {
                Limit = Math.Clamp(s.Limit!.Value, -1e15, 1e15),
                Color = ElementAnimation.Hex(s.Color),
                Blink = s.Blink is "slow" or "fast" ? s.Blink : "none"
            })
            .OrderBy(s => s.Limit)
            .Take(8)
            .ToList();
        if (stops.Count == 0) return null;
        var c = new ColorAnimation { Tag = tag, Fill = a.Fill, Border = a.Border, Text = a.Text, Stops = stops };
        if (!c.Fill && !c.Border && !c.Text) c.Fill = true;
        return c;
    }
}

public class ColorStop
{
    /// <summary>Ngưỡng dưới (≥). Để trống (null) = bỏ qua dòng này.</summary>
    public double? Limit { get; set; }
    public string? Color { get; set; }
    /// <summary>"none" | "slow" (≈1 giây) | "fast" (≈0.4 giây).</summary>
    public string Blink { get; set; } = "none";
}
