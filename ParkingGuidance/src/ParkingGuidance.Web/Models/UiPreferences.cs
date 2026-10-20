using System.Text.RegularExpressions;

namespace ParkingGuidance.Web.Models;

/// <summary>Giao diện dùng chung cho MỌI máy truy cập hệ thống (nền sáng/tối, ngôn ngữ, font, cỡ chữ, màu nhấn).
/// Lưu trong settings.json; máy nào chọn "Chỉ máy này" thì dùng cài đặt riêng trong trình duyệt và bỏ qua mục này.</summary>
public class UiPreferences
{
    private static readonly string[] Themes = { "dark", "light", "auto" };
    private static readonly string[] Langs = { "vi", "en" };
    private static readonly string[] Fonts = { "segoe", "roboto", "arial", "verdana", "georgia", "mono" };

    /// <summary>"dark" | "light" | "auto" (theo hệ điều hành của từng máy).</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>"vi" | "en".</summary>
    public string Lang { get; set; } = "vi";
    /// <summary>Khoá font: segoe, roboto, arial, verdana, georgia, mono.</summary>
    public string Font { get; set; } = "segoe";
    /// <summary>Cỡ chữ theo % (85–130).</summary>
    public int Size { get; set; } = 100;
    /// <summary>Màu nhấn dạng #rrggbb.</summary>
    public string Accent { get; set; } = "#3b82f6";

    public UiPreferences Sanitized() => new()
    {
        Theme = Themes.Contains(Theme?.ToLowerInvariant()) ? Theme!.ToLowerInvariant() : "dark",
        Lang = Langs.Contains(Lang?.ToLowerInvariant()) ? Lang!.ToLowerInvariant() : "vi",
        Font = Fonts.Contains(Font?.ToLowerInvariant()) ? Font!.ToLowerInvariant() : "segoe",
        Size = Math.Clamp(Size, 85, 130),
        Accent = Accent is not null && Regex.IsMatch(Accent, "^#[0-9a-fA-F]{6}$") ? Accent.ToLowerInvariant() : "#3b82f6"
    };
}
