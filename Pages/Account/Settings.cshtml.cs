using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Account;
[RequestSizeLimit(Manage.EditorModel.UploadRequestLimit), RequestFormLimits(MultipartBodyLengthLimit = Manage.EditorModel.UploadRequestLimit)]
public sealed class SettingsModel(UserStore users, MediaStore media) : PageModel
{
    public BlogUser Account { get; private set; } = null!;
    [BindProperty] public ProfileInput Profile { get; set; } = new();
    [BindProperty] public PasswordInput Password { get; set; } = new();
    [BindProperty] public IFormFile? Avatar { get; set; }
    [BindProperty] public IFormFile? ProfileBanner { get; set; }
    [BindProperty] public bool RemoveAvatar { get; set; }
    [BindProperty] public bool RemoveProfileBanner { get; set; }
    [BindProperty] public string? NewUsername { get; set; }
    [BindProperty] public string? UsernamePassword { get; set; }
    public async Task<IActionResult> OnPostUsernameAsync()
    {
        Account = users.FindById(UserStore.UserId(User)!)!;
        ModelState.Clear();
        Profile = new() { DisplayName = Account.DisplayName, Bio = Account.Bio };
        if (!users.VerifyPassword(Account, UsernamePassword ?? ""))
        {
            ModelState.AddModelError(string.Empty, "The current password is incorrect.");
            return Page();
        }
        try
        {
            var renamed = users.ChangeUsername(Account, NewUsername ?? "");
            if (renamed.Username == Account.Username) { TempData["Notice"] = "That is already your username."; return RedirectToPage(); }
            await AccountSession.SignInAsync(HttpContext, renamed);
            TempData["Notice"] = $"Your username is now {renamed.Username}. Sign in with it from now on.";
            return RedirectToPage();
        }
        catch (ArgumentException error) { ModelState.AddModelError(string.Empty, error.Message); }
        return Page();
    }
    public async Task<IActionResult> OnPostPicturesAsync()
    {
        Account = users.FindById(UserStore.UserId(User)!)!;
        ModelState.Clear();
        Profile = new() { DisplayName = Account.DisplayName, Bio = Account.Bio };
        var uploads = (Avatar is null ? 0 : 1) + (ProfileBanner is null ? 0 : 1);
        if (uploads > 0 && media.CountRecent(Account.Id, TimeSpan.FromHours(1)) + uploads > MediaStore.UploadsPerHour)
        {
            ModelState.AddModelError(string.Empty, "You have uploaded a lot of images recently. Try again in an hour.");
            return Page();
        }
        // Process both images before changing anything, so one bad file does not half-apply.
        ProcessedImage? avatar = null, banner = null;
        try
        {
            if (Avatar is not null) avatar = await ImageProcessor.ProcessAsync(Avatar, ImageKind.Avatar, HttpContext.RequestAborted);
            if (ProfileBanner is not null) banner = await ImageProcessor.ProcessAsync(ProfileBanner, ImageKind.ProfileBanner, HttpContext.RequestAborted);
        }
        catch (ImageUploadException error)
        {
            ModelState.AddModelError(string.Empty, error.Message);
            return Page();
        }
        if (avatar is not null) media.Delete(users.SetAvatar(Account, media.Save(Account.Id, MediaStore.Avatar, avatar).Id));
        else if (RemoveAvatar) media.Delete(users.SetAvatar(Account, null));
        if (banner is not null) media.Delete(users.SetBanner(Account, media.Save(Account.Id, MediaStore.ProfileBanner, banner).Id));
        else if (RemoveProfileBanner) media.Delete(users.SetBanner(Account, null));
        TempData["Notice"] = "Pictures updated.";
        return RedirectToPage();
    }
    public void OnGet()
    {
        Account = users.FindById(UserStore.UserId(User)!)!;
        Profile = new() { DisplayName = Account.DisplayName, Bio = Account.Bio };
    }
    public IActionResult OnPostProfile()
    {
        Account = users.FindById(UserStore.UserId(User)!)!;
        ModelState.Clear();
        TryValidateModel(Profile, nameof(Profile));
        if (!ModelState.IsValid) return Page();
        users.UpdateProfile(Account, Profile.DisplayName, Profile.Bio ?? "");
        TempData["Notice"] = "Profile updated.";
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostPasswordAsync()
    {
        Account = users.FindById(UserStore.UserId(User)!)!;
        ModelState.Clear();
        TryValidateModel(Password, nameof(Password));
        Profile = new() { DisplayName = Account.DisplayName, Bio = Account.Bio };
        if (!ModelState.IsValid) return Page();
        if (!users.VerifyPassword(Account, Password.Current)) ModelState.AddModelError(string.Empty, "The current password is incorrect.");
        if (!ModelState.IsValid) return Page();
        users.SetPassword(Account, Password.New);
        await HttpContext.SignOutAsync();
        return LocalRedirect("/account/login/");
    }
    public sealed class ProfileInput
    {
        [Required, StringLength(80)] public string DisplayName { get; set; } = "";
        [StringLength(1000)] public string Bio { get; set; } = "";
    }
    public sealed class PasswordInput
    {
        [Required, StringLength(128)] public string Current { get; set; } = "";
        [Required, StringLength(128, MinimumLength = 12)] public string New { get; set; } = "";
        [Required, Compare(nameof(New))] public string Confirm { get; set; } = "";
    }
}
