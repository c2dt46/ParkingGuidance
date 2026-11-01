using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ParkingGuidance.Web.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    [BindProperty] public string Account { get; set; } = "User";
    [BindProperty] public string? Password { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        // Da dang nhap roi thi vao thang trang chu
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect("/");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var expected = Accounts.GetPassword(Account);
        if (expected is null || !Accounts.Matches(Password, expected))
        {
            Error = "Tài khoản hoặc mật khẩu không đúng. Kiểm tra lại và thử lần nữa.";
            return Page();
        }

        var name = Accounts.CanonicalName(Account);
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, name)      // "User" | "Megatech" | "Lic" | "DEV"
            },
            CookieAuthenticationDefaults.AuthenticationScheme);

        // Tài khoản Lic (bảo mật cao): KHÔNG nhớ đăng nhập — cookie phiên (đóng trình duyệt là mất), tối đa 60 phút, và mất hiệu lực khi
        // chương trình chạy lại (claim mã lần chạy, kiểm tra ở Program.cs). Tài khoản khác: nhớ 8 giờ.
        var isLic = string.Equals(name, "Lic", StringComparison.Ordinal);
        if (isLic) identity.AddClaim(new Claim(ParkingGuidance.Web.Services.SessionBoot.ClaimType, ParkingGuidance.Web.Services.SessionBoot.Id));
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = !isLic,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(isLic ? 60 : 8 * 60),
                AllowRefresh = !isLic
            });

        return LocalRedirect(!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/");
    }

    // Nut "Dang xuat": <form method="post" asp-page="/Login" asp-page-handler="Logout">
    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}

internal static class Accounts
{
    // Mat khau Megatech = "megatech" + ngay hien tai dang ddMMyy (vi du 25/10/2026 -> megatech251026)
    private const string MegatechDateFormat = "ddMMyy";

    public static string? GetPassword(string? account) => account?.Trim().ToLowerInvariant() switch
    {
        "user"     => "888",
        "megatech" => "megatech" + DateTime.Now.ToString(MegatechDateFormat, CultureInfo.InvariantCulture),
        "dev"      => "1",
        "lic"      => "2",          // TẠM THỜI — đổi mật khẩu thật trước khi đưa vào sử dụng
        _          => null
    };

    public static string CanonicalName(string account) => account.Trim().ToLowerInvariant() switch
    {
        "megatech" => "Megatech",
        "dev"      => "DEV",
        "lic"      => "Lic",
        _          => "User"
    };

    // So sanh thoi gian co dinh
    public static bool Matches(string? input, string expected)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(input ?? ""));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
