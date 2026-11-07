namespace ParkingGuidance.Web.Services;

/// <summary>Mã của LẦN CHẠY hiện tại của chương trình (đổi mỗi lần khởi động). Phiên đăng nhập của tài khoản Lic gắn mã này nên tự mất
/// hiệu lực khi chương trình chạy lại — tài khoản Lic không được "nhớ" đăng nhập.</summary>
public static class SessionBoot
{
    public static readonly string Id = Guid.NewGuid().ToString("N");
    public const string ClaimType = "pgs_boot";
}

/// <summary>Chính sách NHỚ ĐĂNG NHẬP. Chỉ tài khoản User và Megatech (quản trị) được nhớ lâu: mở lại chương trình / trình duyệt vẫn vào thẳng, không hỏi mật khẩu,
/// cho tới khi bấm "Đăng xuất" hoặc hết hạn. DEV giữ 8 giờ; Lic không được nhớ (xem Login.cshtml.cs).
/// Thời hạn nhớ chỉnh ở appsettings: <c>"Auth": { "RememberDays": 30 }</c> (1–365 ngày, mặc định 30). Thời hạn tự gia hạn mỗi khi dùng (sliding).</summary>
public static class LoginPolicy
{
    public const int DefaultRememberDays = 30;
    public static readonly TimeSpan DevSpan = TimeSpan.FromHours(8);
    public static readonly TimeSpan LicSpan = TimeSpan.FromMinutes(60);

    public static TimeSpan RememberSpan(IConfiguration config)
    {
        var days = config.GetValue<int?>("Auth:RememberDays") ?? DefaultRememberDays;
        return TimeSpan.FromDays(Math.Clamp(days, 1, 365));
    }
}
