using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages;
public sealed class TagModel(BlogStore blog) : PageModel
{
    public string Tag { get; private set; } = "";
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public IActionResult OnGet(string tag)
    {
        if (!blog.Tags.Contains(tag)) return NotFound();
        Tag = tag;
        Posts = blog.Posts.Where(post => post.Tags.Contains(tag)).ToList();
        return Page();
    }
}
