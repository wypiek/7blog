using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Manage;
public sealed class IndexModel(PostStore posts) : PageModel
{
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public void OnGet() => Posts = posts.ByAuthor(UserStore.UserId(User)!);
}
