using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Manage;
public sealed class DeleteModel(PostStore posts, MediaStore media) : PageModel
{
    public BlogPost Post { get; private set; } = null!;
    [BindProperty] public int Version { get; set; }
    public IActionResult OnGet(string id)
    {
        var post = posts.FindOwned(id, UserStore.UserId(User)!);
        if (post is null) return NotFound();
        Post = post; Version = post.Version;
        return Page();
    }
    public IActionResult OnPost(string id)
    {
        var post = posts.FindOwned(id, UserStore.UserId(User)!);
        if (post is null) return NotFound();
        Post = post;
        if (!posts.DeleteOwned(id, UserStore.UserId(User)!, Version))
        {
            Response.StatusCode = 409;
            ModelState.AddModelError(string.Empty, "The post changed. Reload this page before confirming deletion.");
            return Page();
        }
        media.DeleteForPost(id);
        TempData["Notice"] = "Post deleted.";
        return LocalRedirect("/manage/");
    }
}
