using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SevenBlog.Services;

public sealed record BlogPost(string Id, string Slug, string Title, string Description, string Markdown,
    IReadOnlyList<string> Tags, bool Featured, DateTimeOffset? PublishedAt, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int Version, string? AuthorId, string AuthorName, string? AuthorUsername,
    string? BannerId = null, string? AuthorAvatarId = null)
{
    public string Url => $"/posts/{Slug}/";
    public string? AuthorUrl => AuthorUsername is null ? null : $"/users/{AuthorUsername}/";
    public string AuthorAvatarUrl => AuthorAvatarId is null ? BlogUser.DefaultAvatarUrl : MediaStore.Url(AuthorAvatarId);
    public string? BannerUrl => BannerId is null ? null : MediaStore.Url(BannerId);
    public DateTimeOffset Date => PublishedAt ?? CreatedAt;
    public string DisplayDate => Date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    public int ReadingMinutes => Math.Max(1, (int)Math.Ceiling(Regex.Matches(Markdown, @"\S+").Count / 220.0));
    public string Html => MarkdownRenderer.Render(Markdown);
    public string Status => PublishedAt is null ? "Draft" : PublishedAt > DateTimeOffset.UtcNow ? "Scheduled" : "Published";
}

public sealed record PostWrite(string Title, string Description, string Slug, string Markdown, string[] Tags, bool Publish, bool Featured = false);
public sealed class PostConflictException(string message) : Exception(message);

public sealed class PostStore(BlogDatabase database, SiteSettingsStore site)
{
    private const string Select = """
        SELECT p.id,p.slug,p.title,p.description,p.markdown,p.tags,p.featured,p.published_at,
        p.created_at,p.updated_at,p.version,p.author_id,COALESCE(u.display_name,$fallback),u.username,
        p.banner_id,u.avatar_id
        FROM posts p LEFT JOIN users u ON u.id=p.author_id
        """;
    public IReadOnlyList<BlogPost> Published() => Query("WHERE p.published_at IS NOT NULL AND p.published_at <= $now ORDER BY p.published_at DESC,p.slug");
    public IReadOnlyList<BlogPost> ByAuthor(string id) => Query("WHERE p.author_id=$author ORDER BY p.updated_at DESC", ("$author", id));
    public BlogPost? FindOwned(string id, string authorId) => Query("WHERE p.id=$id AND p.author_id=$author", ("$id", id), ("$author", authorId)).FirstOrDefault();

    // Administrator moderation: these ignore ownership, so only the admin panel may call them.
    public IReadOnlyList<BlogPost> All() => Query("ORDER BY p.updated_at DESC");
    public BlogPost? Find(string id) => Query("WHERE p.id=$id", ("$id", id)).FirstOrDefault();
    public bool Moderate(string id, int version, string authorId, bool featured, bool published)
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE posts SET author_id=$author, featured=$featured, version=version+1,
                published_at=CASE WHEN $published=1 THEN COALESCE(published_at,$now) ELSE NULL END
            WHERE id=$id AND version=$version
            """;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$author", authorId); command.Parameters.AddWithValue("$featured", featured ? 1 : 0);
        command.Parameters.AddWithValue("$published", published ? 1 : 0);
        command.Parameters.AddWithValue("$now", BlogDatabase.Timestamp(DateTimeOffset.UtcNow));
        return command.ExecuteNonQuery() == 1;
    }
    public bool Delete(string id, int version)
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM posts WHERE id=$id AND version=$version";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$version", version);
        return command.ExecuteNonQuery() == 1;
    }

    public BlogPost Save(string authorId, string? id, int version, PostWrite input)
    {
        Validate(input);
        var existing = id is null ? null : FindOwned(id, authorId);
        if (id is not null && existing is null) throw new KeyNotFoundException("Post not found.");
        var now = DateTimeOffset.UtcNow;
        var published = input.Publish ? existing?.PublishedAt ?? now : (DateTimeOffset?)null;
        id ??= Guid.NewGuid().ToString("N");
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = existing is null ? """
            INSERT INTO posts(id,slug,author_id,title,description,markdown,tags,featured,published_at,created_at,updated_at)
            VALUES($id,$slug,$author,$title,$description,$markdown,$tags,$featured,$published,$now,$now)
            """ : """
            UPDATE posts SET slug=$slug,title=$title,description=$description,markdown=$markdown,tags=$tags,
                featured=$featured,published_at=$published,updated_at=$now,version=version+1
            WHERE id=$id AND author_id=$author AND version=$version
            """;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$slug", input.Slug);
        command.Parameters.AddWithValue("$author", authorId); command.Parameters.AddWithValue("$title", input.Title.Trim());
        command.Parameters.AddWithValue("$description", input.Description.Trim()); command.Parameters.AddWithValue("$markdown", input.Markdown);
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(input.Tags.Distinct().ToArray()));
        command.Parameters.AddWithValue("$featured", input.Featured ? 1 : 0);
        command.Parameters.AddWithValue("$published", published.HasValue ? BlogDatabase.Timestamp(published.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$now", BlogDatabase.Timestamp(now)); command.Parameters.AddWithValue("$version", version);
        try
        {
            if (command.ExecuteNonQuery() == 0) throw new PostConflictException("A newer version of this post exists. Reload the page before saving.");
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new ArgumentException("Another post already uses that address."); }
        return FindOwned(id, authorId)!;
    }

    public bool DeleteOwned(string id, string authorId, int version)
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM posts WHERE id=$id AND author_id=$author AND version=$version";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$author", authorId);
        command.Parameters.AddWithValue("$version", version);
        return command.ExecuteNonQuery() == 1;
    }

    // Returns the previous banner id so the caller can delete its file.
    public string? SetBanner(string id, string authorId, string? mediaId)
    {
        using var connection = database.Open(); using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT banner_id FROM posts WHERE id=$id AND author_id=$author";
        read.Parameters.AddWithValue("$id", id); read.Parameters.AddWithValue("$author", authorId);
        var previous = read.ExecuteScalar() as string;
        using var write = connection.CreateCommand(); write.Transaction = transaction;
        write.CommandText = "UPDATE posts SET banner_id=$banner WHERE id=$id AND author_id=$author";
        write.Parameters.AddWithValue("$id", id); write.Parameters.AddWithValue("$author", authorId);
        write.Parameters.AddWithValue("$banner", (object?)mediaId ?? DBNull.Value);
        write.ExecuteNonQuery(); transaction.Commit();
        return previous;
    }

    private IReadOnlyList<BlogPost> Query(string suffix, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = Select + " " + suffix;
        command.Parameters.AddWithValue("$fallback", site.Current.Author);
        command.Parameters.AddWithValue("$now", BlogDatabase.Timestamp(DateTimeOffset.UtcNow));
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        using var reader = command.ExecuteReader(); var posts = new List<BlogPost>();
        while (reader.Read()) posts.Add(new BlogPost(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), Array.AsReadOnly(JsonSerializer.Deserialize<string[]>(reader.GetString(5))!),
            reader.GetBoolean(6), reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)), DateTimeOffset.Parse(reader.GetString(9)), reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15)));
        return posts;
    }

    public static void Validate(PostWrite input)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 180) throw new ArgumentException("A title of 1–180 characters is required.");
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Trim().Length > 400) throw new ArgumentException("A description of 1–400 characters is required.");
        if (input.Slug.Length > 100 || !Regex.IsMatch(input.Slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$")) throw new ArgumentException("Use lowercase letters, digits and hyphens in the post address (at most 100 characters).");
        if (string.IsNullOrWhiteSpace(input.Markdown) || input.Markdown.Length > 100_000) throw new ArgumentException("Post text is required and may contain at most 100,000 characters.");
        if (input.Tags.Length is < 1 or > 10 || input.Tags.Any(tag => tag.Length > 40 || !Regex.IsMatch(tag, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))) throw new ArgumentException("Use 1–10 topics, each with lowercase letters, digits and hyphens (at most 40 characters).");
    }
}
