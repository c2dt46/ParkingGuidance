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
                new Claim(ClaimTypes.Role, name)      // "User" | "Megatech" | "DEV"
            },
            CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

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
        _          => null
    };

    public static string CanonicalName(string account) => account.Trim().ToLowerInvariant() switch
    {
        "megatech" => "Megatech",
        "dev"      => "DEV",
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
