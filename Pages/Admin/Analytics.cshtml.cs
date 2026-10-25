using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SevenBlog.Services;

namespace SevenBlog.Pages.Admin;

public sealed class AnalyticsModel(AnalyticsCollector analytics, BlogStore blog) : PageModel
{
    public static readonly int[] Ranges = [7, 30, 90];
    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;
    public AnalyticsReport Report { get; private set; } = null!;
    public IReadOnlyDictionary<string, string> Titles { get; private set; } = new Dictionary<string, string>();

    public void OnGet()
    {
        if (!Ranges.Contains(Days)) Days = 30;
        Report = analytics.Report(Days);
        Titles = blog.Posts.ToDictionary(post => post.Url, post => post.Title);
    }

    public string Label(string path) => path == "/" ? "Home page" : Titles.GetValueOrDefault(path) ?? path;

    // Bar length for the 7.css progress bar, as a share of the largest value in the same list.
    public static string Width(int value, int max) =>
        max <= 0 ? "0%" : Math.Round(100.0 * value / max).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
}
