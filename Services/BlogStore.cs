namespace SevenBlog.Services;

// A request-scoped public snapshot keeps page content, counts, and topics consistent.
public sealed class BlogStore(PostStore store)
{
    private IReadOnlyList<BlogPost>? posts;
    public IReadOnlyList<BlogPost> Posts => posts ??= store.Published();
    public IReadOnlyList<string> Tags => Posts.SelectMany(post => post.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public BlogPost? Find(string slug) => Posts.FirstOrDefault(post => post.Slug == slug);
    public IReadOnlyList<BlogPost> Search(string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];
        return Posts.Where(post => words.All(word =>
            $"{post.Title} {post.Description} {post.AuthorName} {string.Join(' ', post.Tags)} {post.Markdown}"
                .Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    public static string TagLabel(string tag) => tag.Replace('-', ' ');
}

public sealed class PostMetadata
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public string[] Tags { get; init; } = [];
    public bool Featured { get; init; }
    public bool Draft { get; init; }
}
