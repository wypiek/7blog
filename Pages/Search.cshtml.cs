using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages;
public sealed class SearchModel(BlogStore blog) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }
    public IReadOnlyList<BlogPost> Results { get; private set; } = [];
    public void OnGet()
    {
        Query = (Query ?? "").Trim();
        if (Query.Length > 200) Query = Query[..200];
        Results = blog.Search(Query);
    }
}
