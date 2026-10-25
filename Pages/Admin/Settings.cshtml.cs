using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Admin;

[RequestSizeLimit(Manage.EditorModel.UploadRequestLimit), RequestFormLimits(MultipartBodyLengthLimit = Manage.EditorModel.UploadRequestLimit)]
public sealed class SettingsModel(SiteSettingsStore site, MediaStore media) : PageModel
{
    public SiteSettings Current { get; private set; } = null!;
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string Author { get; set; } = "";
    [BindProperty] public string? About { get; set; }
    [BindProperty] public IFormFile? Icon { get; set; }
    [BindProperty] public IFormFile? Header { get; set; }
    [BindProperty] public bool RemoveIcon { get; set; }
    [BindProperty] public bool RemoveHeader { get; set; }

    public void OnGet()
    {
        Current = site.Current;
        (Name, Description, Author, About) = (Current.Name, Current.Description, Current.Author, Current.About);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Current = site.Current;
        var adminId = UserStore.UserId(User)!;
        // Process both images before saving anything, so one bad file does not half-apply.
        ProcessedImage? icon = null, header = null;
        try
        {
            if (Icon is not null) icon = await ImageProcessor.ProcessAsync(Icon, ImageKind.SiteIcon, HttpContext.RequestAborted);
            if (Header is not null) header = await ImageProcessor.ProcessAsync(Header, ImageKind.SiteHeader, HttpContext.RequestAborted);
        }
        catch (ImageUploadException error)
        {
            ModelState.AddModelError(string.Empty, error.Message);
            return Page();
        }
        var iconId = icon is not null ? media.Save(adminId, MediaStore.SiteIcon, icon).Id : RemoveIcon ? null : Current.IconId;
        var headerId = header is not null ? media.Save(adminId, MediaStore.SiteHeader, header).Id : RemoveHeader ? null : Current.HeaderId;
        try
        {
            site.Save(new SiteSettings(Name ?? "", Description ?? "", Author ?? "", About ?? "", iconId, headerId));
        }
        catch (ArgumentException error)
        {
            // Nothing was saved, so discard any newly processed images.
            if (iconId != Current.IconId) media.Delete(iconId);
            if (headerId != Current.HeaderId) media.Delete(headerId);
            ModelState.AddModelError(string.Empty, error.Message);
            return Page();
        }
        if (iconId != Current.IconId) media.Delete(Current.IconId);
        if (headerId != Current.HeaderId) media.Delete(Current.HeaderId);
        TempData["Notice"] = "Site settings saved.";
        return LocalRedirect("/admin/settings/");
    }
}
