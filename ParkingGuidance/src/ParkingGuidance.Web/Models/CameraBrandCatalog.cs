namespace ParkingGuidance.Web.Models;

/// <summary>1 hãng camera IP trong danh mục lựa chọn của hệ thống.</summary>
public class CameraBrandInfo
{
    public string Name { get; set; } = "";

    /// <summary>Từ khoá dùng để nhận diện hãng trong chuỗi Scopes/thông tin ONVIF trả về
    /// (vd Scopes có "onvif://www.onvif.org/name/Hikvision" → khớp từ khoá "hikvision").</summary>
    public string OnvifKeyword { get; set; } = "";
}

/// <summary>
/// Danh mục các hãng camera IP thông dụng cho hệ thống giám sát bãi xe/ANPR.
/// Nhóm phổ biến tại Việt Nam (giá tốt, dễ mua, hỗ trợ ONVIF): Hikvision, Dahua, KBVision,
/// CP Plus, Avtech, TVT (nhiều hãng trong nhóm này dùng chung nền tảng OEM Dahua/Hikvision).
/// Nhóm quốc tế/phân khúc cao (ONVIF chuẩn tốt hơn, hay dùng cho ANPR chuyên nghiệp):
/// Axis Communications, Bosch, Panasonic i-PRO, Hanwha Vision (Samsung Techwin), Uniview, Vivotek.
/// </summary>
public static class CameraBrandCatalog
{
    public static readonly List<CameraBrandInfo> All = new()
    {
        new() { Name = "Hikvision", OnvifKeyword = "hikvision" },
        new() { Name = "Dahua", OnvifKeyword = "dahua" },
        new() { Name = "KBVision", OnvifKeyword = "kbvision" },
        new() { Name = "CP Plus", OnvifKeyword = "cp plus" },
        new() { Name = "Avtech", OnvifKeyword = "avtech" },
        new() { Name = "TVT", OnvifKeyword = "tvt" },
        new() { Name = "Axis Communications", OnvifKeyword = "axis" },
        new() { Name = "Bosch", OnvifKeyword = "bosch" },
        new() { Name = "Panasonic i-PRO", OnvifKeyword = "panasonic" },
        new() { Name = "Hanwha Vision (Samsung)", OnvifKeyword = "hanwha" },
        new() { Name = "Uniview (UNV)", OnvifKeyword = "uniview" },
        new() { Name = "Vivotek", OnvifKeyword = "vivotek" },
        new() { Name = "Khác / ONVIF chuẩn", OnvifKeyword = "" },
    };
}
