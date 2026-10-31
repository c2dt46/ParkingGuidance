using System.Security.Claims;

namespace ParkingGuidance.Web.Services.Licensing;

/// <summary>Cấp đăng nhập: 0 = dev, 1 = lic (sẽ tạo sau), 2 = admin (Megatech), 3 = user.</summary>
public static class AccessLevel
{
    public const int Dev = 0;
    public const int Lic = 1;
    public const int Admin = 2;
    public const int User = 3;

    /// <summary>Cấp của người đang đăng nhập, suy ra từ claim Role đã có sẵn (nên phiên đăng nhập cũ vẫn dùng được).</summary>
    public static int Of(ClaimsPrincipal? principal)
    {
        var role = principal?.FindFirst(ClaimTypes.Role)?.Value;
        return role?.Trim().ToLowerInvariant() switch
        {
            "dev" => Dev,
            "lic" => Lic,
            "megatech" => Admin,
            _ => User
        };
    }

    /// <summary>Cấp 0 hoặc 1: bản quyền mặc định đã được cấp, không bao giờ bị kiểm tra, được tạo giấy phép.</summary>
    public static bool IsPrivileged(ClaimsPrincipal? principal) => Of(principal) <= Lic;
}

public enum HardwareKind : byte
{
    Disk = 1,
    Network = 2,
    Board = 3
}

/// <summary>Một số seri phần cứng đã chuẩn hoá (chỉ 0-9 A-Z, viết hoa).</summary>
public sealed record HardwareId(HardwareKind Kind, string Serial);

public enum LicenseDuration : byte
{
    Month1 = 1,
    Month3 = 2,
    Month6 = 3,
    Year1 = 4,
    Year3 = 5,
    Permanent = 6
}

public static class LicenseDurations
{
    public static bool TryParse(string? code, out LicenseDuration duration)
    {
        switch (code?.Trim().ToLowerInvariant())
        {
            case "1m": duration = LicenseDuration.Month1; return true;
            case "3m": duration = LicenseDuration.Month3; return true;
            case "6m": duration = LicenseDuration.Month6; return true;
            case "1y": duration = LicenseDuration.Year1; return true;
            case "3y": duration = LicenseDuration.Year3; return true;
            case "perm": duration = LicenseDuration.Permanent; return true;
            default: duration = default; return false;
        }
    }

    public static string Code(LicenseDuration d) => d switch
    {
        LicenseDuration.Month1 => "1m",
        LicenseDuration.Month3 => "3m",
        LicenseDuration.Month6 => "6m",
        LicenseDuration.Year1 => "1y",
        LicenseDuration.Year3 => "3y",
        _ => "perm"
    };

    public static string Label(LicenseDuration d) => d switch
    {
        LicenseDuration.Month1 => "1 tháng",
        LicenseDuration.Month3 => "3 tháng",
        LicenseDuration.Month6 => "6 tháng",
        LicenseDuration.Year1 => "1 năm",
        LicenseDuration.Year3 => "3 năm",
        _ => "Vĩnh viễn"
    };

    /// <summary>Số giây SỬ DỤNG được cấp (1 tháng = 30 ngày, 1 năm = 365 ngày). 0 với giấy phép vĩnh viễn.</summary>
    public static long Seconds(LicenseDuration d) => d switch
    {
        LicenseDuration.Month1 => 30L * 86400,
        LicenseDuration.Month3 => 90L * 86400,
        LicenseDuration.Month6 => 180L * 86400,
        LicenseDuration.Year1 => 365L * 86400,
        LicenseDuration.Year3 => 1095L * 86400,
        _ => 0
    };
}

/// <summary>Nội dung 1 giấy phép sau khi giải mã.</summary>
public sealed record LicenseData(ulong Id, LicenseDuration Duration, DateTimeOffset IssuedUtc, IReadOnlyList<HardwareId> Hardware);

public enum LicenseState
{
    Initializing,
    NotActivated,
    /// <summary>Chưa có giấy phép nhưng đang trong thời gian chạy thử của lần khởi động này.</summary>
    Trial,
    Active,
    Permanent,
    Expired,
    HardwareMismatch,
    Tampered
}

public sealed record LicenseStatus(
    LicenseState State,
    bool Operational,
    string Message,
    string? DurationCode,
    string? DurationLabel,
    long TotalSeconds,
    long ConsumedSeconds,
    long RemainingSeconds,
    int Matched,
    int Required,
    DateTimeOffset? ActivatedUtc,
    IReadOnlyList<HardwareMatch> Hardware);

public sealed record HardwareMatch(HardwareKind Kind, string Serial, bool Match);

public sealed record RequestResult(bool Ok, string? Text, IReadOnlyList<HardwareId> Hardware, string? Error);

public sealed record RequestInfoResult(bool Ok, IReadOnlyList<HardwareId> Hardware, string? Error);

public sealed record IssueResult(bool Ok, string? Text, string? DurationLabel, IReadOnlyList<HardwareId> Hardware, string? Error);

public sealed record PreviewResult(
    bool Ok,
    string? Error,
    string? DurationCode,
    string? DurationLabel,
    IReadOnlyList<HardwareMatch> Hardware,
    int Matched,
    int Required,
    bool CanActivate,
    string? Message);

public sealed record ActivateResult(bool Ok, string Message);
