using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Admin;

public sealed class PostModel(PostStore posts, UserStore users, MediaStore media) : PageModel
{
    public BlogPost Post { get; private set; } = null!;
    public IReadOnlyList<BlogUser> Authors { get; private set; } = [];
    [BindProperty] public int Version { get; set; }
    [BindProperty] public string AuthorId { get; set; } = "";
    [BindProperty] public string Status { get; set; } = "Draft";
    [BindProperty] public bool Featured { get; set; }
    [BindProperty] public bool ConfirmDelete { get; set; }

    private bool Load(string id)
    {
        var post = posts.Find(id);
        if (post is null) return false;
        Post = post;
        Authors = users.List().Where(user => !user.Disabled || user.Id == post.AuthorId).ToArray();
        return true;
    }

    public IActionResult OnGet(string id)
    {
        if (!Load(id)) return NotFound();
        Version = Post.Version; AuthorId = Post.AuthorId ?? ""; Featured = Post.Featured;
        Status = Post.PublishedAt is null ? "Draft" : "Published";
        return Page();
    }

    public IActionResult OnPostModerate(string id)
    {
        if (!Load(id)) return NotFound();
        if (Authors.FirstOrDefault(user => user.Id == AuthorId) is null)
            ModelState.AddModelError(string.Empty, "Choose an existing author.");
        else if (Status is not ("Draft" or "Published"))
            ModelState.AddModelError(string.Empty, "Choose Draft or Published.");
        else if (!posts.Moderate(Post.Id, Version, AuthorId, Featured, Status == "Published"))
        {
            Response.StatusCode = 409;
            ModelState.AddModelError(string.Empty, "The post changed while you were editing. Reload the page and try again.");
        }
        else
        {
            if (AuthorId != Post.AuthorId) media.TransferPost(Post.Id, AuthorId);
            TempData["Notice"] = "Post updated.";
            return LocalRedirect($"/admin/posts/{Post.Id}/");
        }
        return Page();
    }

    public IActionResult OnPostDelete(string id)
    {
        if (!Load(id)) return NotFound();
        if (!ConfirmDelete) ModelState.AddModelError(string.Empty, "Tick the box to confirm that the post should be deleted.");
        else if (!posts.Delete(Post.Id, Version))
        {
            Response.StatusCode = 409;
            ModelState.AddModelError(string.Empty, "The post changed while you were looking at it. Reload the page before deleting.");
        }
        else
        {
            media.DeleteForPost(Post.Id);
            TempData["Notice"] = $"Deleted “{Post.Title}”.";
            return LocalRedirect("/admin/posts/");
        }
        return Page();
    }
}
