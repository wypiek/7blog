namespace SevenBlog.Services;

public sealed class BlogOptions
{
    public string Name { get; set; } = "7blog";
    public string SiteUrl { get; set; } = "http://localhost:8080";
    public string Author { get; set; } = "The 7blog author";
    public string Description { get; set; } = "Notes, projects, and other things.";
    public string ContentPath { get; set; } = "content/posts";
    public string DataPath { get; set; } = ".data";
    public string Absolute(string path) => new Uri(new Uri(SiteUrl.TrimEnd('/') + "/"), path.TrimStart('/')).AbsoluteUri;
}
