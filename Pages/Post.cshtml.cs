using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages;

public sealed class PostModel(BlogStore blog, UserStore users) : PageModel
{
    public BlogPost Post { get; private set; } = null!;
    public BlogPost? Next { get; private set; }
    public BlogUser? Author { get; private set; }
    public IActionResult OnGet(string slug)
    {
        var post = blog.Find(slug);
        if (post is null) return NotFound();
        Post = post;
        Author = post.AuthorId is null ? null : users.FindById(post.AuthorId);
        Next = blog.Posts.SkipWhile(item => item.Slug != slug).Skip(1).FirstOrDefault();
        return Page();
    }
}
