using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Admin;

public sealed class PostsModel(PostStore posts) : PageModel
{
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public void OnGet() => Posts = posts.All();
}
