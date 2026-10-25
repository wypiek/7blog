using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Manage;

[RequestSizeLimit(UploadRequestLimit), RequestFormLimits(MultipartBodyLengthLimit = UploadRequestLimit)]
public sealed partial class EditorModel(PostStore posts, MediaStore media, ILogger<EditorModel> logger) : PageModel
{
    public const long UploadRequestLimit = 2 * ImageProcessor.MaxUploadBytes + 1024 * 1024;
    [BindProperty] public EditorInput Input { get; set; } = new();
    [BindProperty] public IFormFile? Banner { get; set; }
    [BindProperty] public bool RemoveBanner { get; set; }
    [BindProperty] public IFormFile? Attachment { get; set; }
    public bool IsNew { get; private set; }
    public bool ShowPreview { get; private set; }
    public string PreviewHtml { get; private set; } = "";
    public BlogPost? Existing { get; private set; }
    public IReadOnlyList<MediaItem> Attachments { get; private set; } = [];
    private string UserId => UserStore.UserId(User)!;
    private bool Load(string? id)
    {
        IsNew = id is null;
        Existing = id is null ? null : posts.FindOwned(id, UserId);
        return id is null || Existing is not null;
    }
    private PageResult EditorPage()
    {
        Attachments = media.ForEditor(UserId, Existing?.Id, Input.Markdown);
        return Page();
    }
    public IActionResult OnGet(string? id)
    {
        if (!Load(id)) return NotFound();
        if (Existing is not null) Input = new() {
            Title = Existing.Title, Description = Existing.Description, Slug = Existing.Slug,
            Markdown = Existing.Markdown, Tags = string.Join(", ", Existing.Tags),
            Status = Existing.PublishedAt.HasValue ? "Published" : "Draft", Version = Existing.Version, Featured = Existing.Featured
        };
        return EditorPage();
    }
    public async Task<IActionResult> OnPostSaveAsync(string? id)
    {
        if (!Load(id)) return NotFound();
        if (!ModelState.IsValid) return EditorPage();
        ProcessedImage? banner = null;
        if (Banner is not null)
        {
            try { banner = await ProcessUpload(Banner, ImageKind.PostBanner); }
            catch (ImageUploadException error) { ModelState.AddModelError(nameof(Banner), $"Banner: {error.Message}"); return EditorPage(); }
        }
        try
        {
            var post = posts.Save(UserId, id, Input.Version, new PostWrite(
                Input.Title, Input.Description, Input.Slug.Trim(), Input.Markdown,
                Input.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Input.Status == "Published", User.IsInRole("Admin") ? Input.Featured : Existing?.Featured ?? false));
            media.LinkReferenced(UserId, post.Id, post.Markdown);
            if (banner is not null)
                media.Delete(posts.SetBanner(post.Id, UserId, media.Save(UserId, MediaStore.PostBanner, banner, post.Id).Id));
            else if (RemoveBanner)
                media.Delete(posts.SetBanner(post.Id, UserId, null));
            TempData["Notice"] = post.Status == "Published" ? "Post published." : "Draft saved.";
            return RedirectToPage(new { id = post.Id });
        }
        catch (PostConflictException error) { Response.StatusCode = 409; ModelState.AddModelError(string.Empty, error.Message); }
        catch (ArgumentException error) { ModelState.AddModelError(string.Empty, error.Message); }
        return EditorPage();
    }
    public async Task<IActionResult> OnPostUploadAsync(string? id)
    {
        if (!Load(id)) return NotFound();
        var script = Request.Headers["X-Requested-With"] == "7blog-upload";
        ModelState.Clear();
        try
        {
            if (Attachment is null) throw new ImageUploadException("Choose an image to upload.");
            var image = await ProcessUpload(Attachment, ImageKind.Attachment);
            var item = media.Save(UserId, MediaStore.Attachment, image, Existing?.Id);
            var markdown = $"![{AltText(Attachment.FileName)}]({item.Url})";
            if (script) return new JsonResult(new { url = item.Url, markdown, width = item.Width, height = item.Height });
            Input.Markdown = (Input.Markdown ?? "").TrimEnd() + "\n\n" + markdown + "\n";
            TempData["Notice"] = "Image uploaded and added to the end of the post. Move its line to where it should appear, then save.";
        }
        catch (ImageUploadException error)
        {
            if (script) return BadRequest(new { error = error.Message });
            ModelState.AddModelError(nameof(Attachment), error.Message);
        }
        return EditorPage();
    }
    public IActionResult OnPostWrite(string? id)
    {
        if (!Load(id)) return NotFound();
        ModelState.Clear();
        return EditorPage();
    }
    public IActionResult OnPostPreview(string? id)
    {
        if (!Load(id)) return NotFound();
        if ((Input.Markdown?.Length ?? 0) > 100_000) return BadRequest("Post text is too long.");
        PreviewHtml = MarkdownRenderer.Render(Input.Markdown ?? "");
        if (Request.Headers["X-Requested-With"] == "7blog-preview")
        {
            Response.Headers["X-7blog-Preview"] = "true";
            return Content(PreviewHtml, "text/html; charset=utf-8");
        }
        ShowPreview = true;
        ModelState.Clear();
        return EditorPage();
    }
    private async Task<ProcessedImage> ProcessUpload(IFormFile file, ImageKind kind)
    {
        if (media.CountRecent(UserId, TimeSpan.FromHours(1)) >= MediaStore.UploadsPerHour)
            throw new ImageUploadException("You have uploaded a lot of images recently. Try again in an hour.");
        var image = await ImageProcessor.ProcessAsync(file, kind, HttpContext.RequestAborted);
        logger.LogInformation("Compressed {Kind} upload from {Original} to {Stored} bytes.", kind, file.Length, image.Bytes.Length);
        return image;
    }
    private static string AltText(string fileName)
    {
        var alt = MarkdownUnsafe().Replace(Path.GetFileNameWithoutExtension(fileName), " ").Replace('-', ' ').Replace('_', ' ').Trim();
        return alt.Length > 100 ? alt[..100] : alt;
    }
    [GeneratedRegex(@"[\[\]\(\)\\`*!<>]+")] private static partial Regex MarkdownUnsafe();
    public sealed class EditorInput
    {
        [Required, StringLength(180)] public string Title { get; set; } = "";
        [Required, StringLength(400)] public string Description { get; set; } = "";
        [Required, StringLength(100), RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$")] public string Slug { get; set; } = "";
        [Required, StringLength(100_000)] public string Markdown { get; set; } = "";
        [Required, StringLength(420)] public string Tags { get; set; } = "";
        [Required, RegularExpression("^(Draft|Published)$")] public string Status { get; set; } = "Draft";
        [Range(0, int.MaxValue)] public int Version { get; set; }
        public bool Featured { get; set; }
    }
}
