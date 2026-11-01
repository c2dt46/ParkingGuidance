namespace ParkingGuidance.Web.Services;

/// <summary>Mã của LẦN CHẠY hiện tại của chương trình (đổi mỗi lần khởi động). Phiên đăng nhập của tài khoản Lic gắn mã này nên tự mất
/// hiệu lực khi chương trình chạy lại — tài khoản Lic không được "nhớ" đăng nhập.</summary>
public static class SessionBoot
{
    public static readonly string Id = Guid.NewGuid().ToString("N");
    public const string ClaimType = "pgs_boot";
}
