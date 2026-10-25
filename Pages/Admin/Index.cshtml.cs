using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Admin;

public sealed class IndexModel(UserStore users, PostStore posts, MediaStore media, BlogDatabase database) : PageModel
{
    public IReadOnlyList<BlogUser> Users { get; private set; } = [];
    public IReadOnlyList<BlogPost> Posts { get; private set; } = [];
    public (int Count, long Bytes) Images { get; private set; }
    public long DatabaseBytes { get; private set; }
    public void OnGet()
    {
        Users = users.List();
        Posts = posts.All();
        Images = media.Totals();
        DatabaseBytes = Directory.EnumerateFiles(database.DirectoryPath, "7blog.db*").Sum(file => new FileInfo(file).Length);
    }
    public static string Size(long bytes) => bytes < 1024 * 1024
        ? (bytes / 1024.0).ToString("0.# 'KB'", System.Globalization.CultureInfo.InvariantCulture)
        : (bytes / (1024.0 * 1024)).ToString("0.# 'MB'", System.Globalization.CultureInfo.InvariantCulture);
}
