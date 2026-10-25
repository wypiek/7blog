using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Admin;

public sealed class UserModel(UserStore users, PostStore posts, MediaStore media) : PageModel
{
    public BlogUser Account { get; private set; } = null!;
    public int PostCount { get; private set; }
    public bool IsSelf { get; private set; }
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string? Bio { get; set; }
    [BindProperty] public string Rank { get; set; } = "Author";
    [BindProperty] public string? NewPassword { get; set; }
    [BindProperty] public string? ConfirmPassword { get; set; }
    [BindProperty] public bool RemoveAvatar { get; set; }
    [BindProperty] public bool RemoveBanner { get; set; }

    private bool Load(string id)
    {
        var account = users.FindById(id);
        if (account is null) return false;
        Account = account;
        IsSelf = account.Id == UserStore.UserId(User);
        PostCount = posts.All().Count(post => post.AuthorId == account.Id);
        return true;
    }
    private void Fill() { Username = Account.Username; DisplayName = Account.DisplayName; Bio = Account.Bio; Rank = Account.Rank; }

    public IActionResult OnGet(string id)
    {
        if (!Load(id)) return NotFound();
        Fill();
        return Page();
    }

    public async Task<IActionResult> OnPostDetailsAsync(string id)
    {
        if (!Load(id)) return NotFound();
        try
        {
            var rank = UserStore.NormalizeRank(Rank ?? "");
            if (rank != Account.Rank && IsSelf)
                throw new ArgumentException("You cannot change your own rank. Ask another administrator, or use the command line.");
            if (Account.Rank == "Admin" && rank != "Admin" && !Account.Disabled && users.CountActiveAdmins() <= 1)
                throw new ArgumentException("This is the last active administrator. Promote someone else first.");
            if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Trim().Length > 80 || (Bio?.Length ?? 0) > 1000)
                throw new ArgumentException("Display names need 1–80 characters and bios at most 1,000.");
            var renamed = users.ChangeUsername(Account, Username ?? "");
            users.UpdateProfile(Account, DisplayName, Bio ?? "");
            if (rank != Account.Rank) users.SetRank(Account, rank);
            // Keep the current administrator's cookie in step with their new username.
            if (IsSelf && renamed.Username != Account.Username) await AccountSession.SignInAsync(HttpContext, users.FindById(Account.Id)!);
            TempData["Notice"] = rank != Account.Rank
                ? $"Saved {renamed.Username}. Their rank changed, so they have been signed out everywhere."
                : $"Saved {renamed.Username}.";
            return LocalRedirect($"/admin/users/{Account.Id}/");
        }
        catch (ArgumentException error) { ModelState.AddModelError(string.Empty, error.Message); }
        return Page();
    }

    public IActionResult OnPostPassword(string id)
    {
        if (!Load(id)) return NotFound();
        Fill();
        try
        {
            if (IsSelf) throw new ArgumentException("Change your own password in Account settings.");
            if (NewPassword != ConfirmPassword) throw new ArgumentException("The passwords do not match.");
            users.SetPassword(Account, NewPassword ?? "");
            TempData["Notice"] = $"Password reset for {Account.Username}. They have been signed out everywhere.";
            return LocalRedirect($"/admin/users/{Account.Id}/");
        }
        catch (ArgumentException error) { ModelState.AddModelError(string.Empty, error.Message); }
        return Page();
    }

    public IActionResult OnPostStatus(string id)
    {
        if (!Load(id)) return NotFound();
        Fill();
        var disable = !Account.Disabled;
        if (disable && IsSelf) ModelState.AddModelError(string.Empty, "You cannot disable your own account.");
        else if (disable && Account.Rank == "Admin" && users.CountActiveAdmins() <= 1)
            ModelState.AddModelError(string.Empty, "This is the last active administrator and cannot be disabled.");
        if (!ModelState.IsValid) return Page();
        users.SetDisabled(Account, disable);
        TempData["Notice"] = disable ? $"Disabled {Account.Username} and signed them out everywhere." : $"Enabled {Account.Username}.";
        return LocalRedirect($"/admin/users/{Account.Id}/");
    }

    public IActionResult OnPostPictures(string id)
    {
        if (!Load(id)) return NotFound();
        if (RemoveAvatar) media.Delete(users.SetAvatar(Account, null));
        if (RemoveBanner) media.Delete(users.SetBanner(Account, null));
        TempData["Notice"] = RemoveAvatar || RemoveBanner ? "Pictures removed." : "Nothing was selected to remove.";
        return LocalRedirect($"/admin/users/{Account.Id}/");
    }
}
