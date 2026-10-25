using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Admin;

public sealed class UsersModel(UserStore users, PostStore posts) : PageModel
{
    public IReadOnlyList<BlogUser> Users { get; private set; } = [];
    public IReadOnlyDictionary<string, int> PostCounts { get; private set; } = new Dictionary<string, int>();
    public void OnGet()
    {
        Users = users.List();
        PostCounts = posts.All().Where(post => post.AuthorId is not null)
            .GroupBy(post => post.AuthorId!).ToDictionary(group => group.Key, group => group.Count());
    }
}
