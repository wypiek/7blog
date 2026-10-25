using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Admin;

public sealed class NewUserModel(UserStore users) : PageModel
{
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string? DisplayName { get; set; }
    [BindProperty] public string Rank { get; set; } = "Author";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";
    public void OnGet() { }
    public IActionResult OnPost()
    {
        try
        {
            if (Password != Confirm) throw new ArgumentException("The passwords do not match.");
            var user = users.Create(Username ?? "", Password ?? "", Rank, DisplayName);
            users.ClaimUnownedPosts();
            TempData["Notice"] = $"Created {user.Username} ({user.RankLabel}). They can sign in now.";
            return LocalRedirect($"/admin/users/{user.Id}/");
        }
        catch (ArgumentException error) { ModelState.AddModelError(string.Empty, error.Message); }
        return Page();
    }
}
