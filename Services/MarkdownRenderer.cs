using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SevenBlog.Services;

public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoIdentifiers().UsePipeTables().UseTaskLists().DisableHtml().Build();

    public static string Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            var url = link.Url?.Trim() ?? "";
            if (url.Any(char.IsControl) || url.Contains('\\') || url.StartsWith("//", StringComparison.Ordinal)
                || (!url.StartsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out var absolute)
                    && absolute.Scheme is not ("http" or "https")
                    && !(absolute.Scheme == "mailto" && !link.IsImage)))
                link.Url = "#";
            if (link.IsImage)
            {
                var attributes = link.GetAttributes();
                attributes.AddPropertyIfNotExist("loading", "lazy");
                attributes.AddPropertyIfNotExist("decoding", "async");
            }
        }
        return document.ToHtml(Pipeline);
    }
}
