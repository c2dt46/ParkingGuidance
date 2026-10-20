using System.Globalization;
using System.Text.Json.Serialization;

namespace ParkingGuidance.Web.Models;

/// <summary>Kiểu dữ liệu của tag (System tags và Project tags dùng chung).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TagDataType
{
    Integer,
    Boolean,
    Real,
    String
}

/// <summary>Tag do người dùng khai báo (TASKS › Tags › Project Tags). Lưu ở data/project-tags.json.</summary>
public sealed class ProjectTag
{
    /// <summary>Tên tag: chữ/số/gạch dưới, bắt đầu bằng chữ hoặc gạch dưới, tối đa 64 ký tự, không trùng (không phân biệt hoa/thường).</summary>
    public string Name { get; set; } = "";
    public TagDataType Type { get; set; } = TagDataType.Integer;
    /// <summary>Giá trị hiện tại, lưu dạng chuỗi chuẩn hoá: Integer "123", Boolean "0"/"1", Real "1.5" (dấu chấm), String nguyên văn.</summary>
    public string Value { get; set; } = "0";
    public string Description { get; set; } = "";
    /// <summary>"Local" hoặc "Global".</summary>
    public string Scope { get; set; } = "Local";

    public ProjectTag Clone() => new()
    {
        Name = Name, Type = Type, Value = Value, Description = Description, Scope = Scope
    };
}

/// <summary>Kiểm tra / chuẩn hoá giá trị tag theo kiểu dữ liệu.</summary>
public static class TagValue
{
    public const int MaxStringLength = 255;

    public static string DefaultFor(TagDataType type) => type == TagDataType.String ? "" : "0";

    public static bool TryNormalize(TagDataType type, string? raw, out string normalized, out string? error)
    {
        error = null;
        raw ??= "";

        if (type == TagDataType.String)
        {
            normalized = raw;
            if (raw.Length > MaxStringLength)
            {
                error = $"Giá trị chuỗi tối đa {MaxStringLength} ký tự.";
                return false;
            }
            return true;
        }

        var s = raw.Trim();
        normalized = "0";
        if (s.Length == 0) return true; // để trống = giá trị mặc định (0)

        switch (type)
        {
            case TagDataType.Integer:
                if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                {
                    normalized = l.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                error = "Giá trị Integer phải là số nguyên.";
                return false;

            case TagDataType.Boolean:
                if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) { normalized = "1"; return true; }
                if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) { normalized = "0"; return true; }
                error = "Giá trị Boolean chỉ nhận 0/1 (hoặc true/false).";
                return false;

            case TagDataType.Real:
                // Cho phép gõ dấu phẩy thập phân kiểu Việt Nam: "1,5" -> 1.5
                var t = s.Contains('.') ? s : s.Replace(',', '.');
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    && !double.IsNaN(d) && !double.IsInfinity(d))
                {
                    normalized = d.ToString("R", CultureInfo.InvariantCulture);
                    return true;
                }
                error = "Giá trị Real phải là số thực.";
                return false;
        }

        error = "Kiểu dữ liệu không hợp lệ.";
        return false;
    }
}
