using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages;
public sealed class ProfileModel(UserStore users, BlogStore blog) : PageModel
{
    public BlogUser Account { get; private set; } = null!;
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public IActionResult OnGet(string username)
    {
        var user = users.FindByUsername(username);
        if (user is null) return NotFound();
        Account = user;
        Posts = blog.Posts.Where(post => post.AuthorId == user.Id).ToArray();
        return Page();
    }
}
