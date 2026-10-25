using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SevenBlog.Services;

namespace SevenBlog.Pages.Account;

[EnableRateLimiting("login")]
public sealed class LoginModel(UserStore users) : PageModel
{
    [BindProperty] public LoginInput Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? LocalRedirect("/manage/") : Page();
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = users.Authenticate(Input.Username, Input.Password);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Sign-in failed. Check your credentials, or try again later if the account is locked.");
            return Page();
        }
        await AccountSession.SignInAsync(HttpContext, user);
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/manage/");
    }
    public sealed class LoginInput
    {
        [Required, StringLength(32)] public string Username { get; set; } = "";
        [Required, StringLength(128)] public string Password { get; set; } = "";
    }
}
