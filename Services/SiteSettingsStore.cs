using Microsoft.Extensions.Options;

namespace SevenBlog.Services;

public sealed record SiteSettings(string Name, string Description, string Author, string About, string? IconId, string? HeaderId)
{
    public const string DefaultIconUrl = "/favicon.svg";
    public string IconUrl => IconId is null ? DefaultIconUrl : MediaStore.Url(IconId);
    public string? HeaderUrl => HeaderId is null ? null : MediaStore.Url(HeaderId);
}

// Site settings edited in the admin panel. Stored in the settings table; anything not set
// there falls back to configuration (appsettings.json / BLOG_* environment variables).
public sealed class SiteSettingsStore(BlogDatabase database, IOptions<BlogOptions> options)
{
    private const string Prefix = "site.";
    private static readonly string[] Keys = ["name", "description", "author", "about", "icon", "header"];
    private volatile SiteSettings? cached;

    public SiteSettings Current => cached ??= Load();

    private SiteSettings Load()
    {
        var values = new Dictionary<string, string>();
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key,value FROM settings WHERE key LIKE 'site.%'";
            using var reader = command.ExecuteReader();
            while (reader.Read()) values[reader.GetString(0)[Prefix.Length..]] = reader.GetString(1);
        }
        var blog = options.Value;
        return new SiteSettings(values.GetValueOrDefault("name") ?? blog.Name, values.GetValueOrDefault("description") ?? blog.Description,
            values.GetValueOrDefault("author") ?? blog.Author, values.GetValueOrDefault("about") ?? "",
            values.GetValueOrDefault("icon"), values.GetValueOrDefault("header"));
    }

    public void Save(SiteSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Name) || settings.Name.Trim().Length > 60) throw new ArgumentException("The site name needs 1–60 characters.");
        if (settings.Description.Trim().Length > 200) throw new ArgumentException("The tagline may contain at most 200 characters.");
        if (string.IsNullOrWhiteSpace(settings.Author) || settings.Author.Trim().Length > 80) throw new ArgumentException("The default author needs 1–80 characters.");
        if (settings.About.Length > 20_000) throw new ArgumentException("The About page may contain at most 20,000 characters.");
        var values = new Dictionary<string, string?> {
            ["name"] = settings.Name.Trim(), ["description"] = settings.Description.Trim(), ["author"] = settings.Author.Trim(),
            ["about"] = settings.About.Trim(), ["icon"] = settings.IconId, ["header"] = settings.HeaderId
        };
        using var connection = database.Open(); using var transaction = connection.BeginTransaction();
        foreach (var key in Keys)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.Parameters.AddWithValue("$key", Prefix + key);
            if (values[key] is string value)
            {
                command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                command.Parameters.AddWithValue("$value", value);
            }
            else command.CommandText = "DELETE FROM settings WHERE key=$key";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        cached = null;
    }
}
