using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SevenBlog.Services;

public static class AccountSession
{
    public const string AdminPolicy = "Administrator";

    // Issues the browser-session cookie. Also used to refresh claims after a username change.
    public static Task SignInAsync(HttpContext context, BlogUser user)
    {
        var claims = new[] {
            new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Rank), new Claim(UserStore.StampClaim, user.SecurityStamp)
        };
        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false });
    }
}
