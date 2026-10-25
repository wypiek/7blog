using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SevenBlog.Services;

public sealed class BlogDatabase
{
    private readonly string connectionString;
    public string DirectoryPath { get; }

    public BlogDatabase(IWebHostEnvironment environment, IOptions<BlogOptions> options)
        : this(Path.GetFullPath(options.Value.DataPath, environment.ContentRootPath),
            Path.GetFullPath(options.Value.ContentPath, environment.ContentRootPath)) { }

    public BlogDatabase(string directory, string? importDirectory = null)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        connectionString = new SqliteConnectionStringBuilder {
            DataSource = Path.Combine(DirectoryPath, "7blog.db"), ForeignKeys = true,
            DefaultTimeout = 10, Pooling = true
        }.ToString();
        using var connection = Open();
        using (var wal = connection.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; wal.ExecuteNonQuery(); }
        using var transaction = connection.BeginTransaction();
        using var versionQuery = connection.CreateCommand();
        versionQuery.Transaction = transaction;
        versionQuery.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionQuery.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                display_name TEXT NOT NULL, bio TEXT NOT NULL DEFAULT '', rank TEXT NOT NULL CHECK(rank IN ('Admin','Author')),
                password_hash TEXT NOT NULL, security_stamp TEXT NOT NULL, created_at TEXT NOT NULL,
                failed_attempts INTEGER NOT NULL DEFAULT 0, lockout_until TEXT, disabled INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS posts (
                id TEXT PRIMARY KEY, slug TEXT NOT NULL UNIQUE, author_id TEXT REFERENCES users(id),
                title TEXT NOT NULL, description TEXT NOT NULL, markdown TEXT NOT NULL, tags TEXT NOT NULL,
                featured INTEGER NOT NULL DEFAULT 0, published_at TEXT, created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS ix_posts_author ON posts(author_id);
            CREATE INDEX IF NOT EXISTS ix_posts_published ON posts(published_at);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        schema.ExecuteNonQuery();
        // Version 2: uploaded images (profile pictures, banners, post attachments).
        if (version < 2)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE media (
                    id TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES users(id), post_id TEXT,
                    kind TEXT NOT NULL CHECK(kind IN ('attachment','post-banner','avatar','profile-banner')),
                    width INTEGER NOT NULL, height INTEGER NOT NULL, bytes INTEGER NOT NULL, created_at TEXT NOT NULL
                );
                CREATE INDEX ix_media_owner ON media(owner_id, created_at);
                CREATE INDEX ix_media_post ON media(post_id);
                ALTER TABLE posts ADD COLUMN banner_id TEXT;
                ALTER TABLE users ADD COLUMN avatar_id TEXT;
                ALTER TABLE users ADD COLUMN banner_id TEXT;
                PRAGMA user_version=2;
                """;
            migration.ExecuteNonQuery();
        }
        // Version 3: site images (icon, header background) and privacy-friendly analytics.
        // SQLite cannot change a CHECK constraint in place, so the media table is rebuilt.
        if (version < 3)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE media_v3 (
                    id TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES users(id), post_id TEXT,
                    kind TEXT NOT NULL CHECK(kind IN ('attachment','post-banner','avatar','profile-banner','site-icon','site-header')),
                    width INTEGER NOT NULL, height INTEGER NOT NULL, bytes INTEGER NOT NULL, created_at TEXT NOT NULL
                );
                INSERT INTO media_v3 SELECT id,owner_id,post_id,kind,width,height,bytes,created_at FROM media;
                DROP TABLE media;
                ALTER TABLE media_v3 RENAME TO media;
                CREATE INDEX ix_media_owner ON media(owner_id, created_at);
                CREATE INDEX ix_media_post ON media(post_id);
                CREATE TABLE analytics_pages (day TEXT NOT NULL, path TEXT NOT NULL, views INTEGER NOT NULL, PRIMARY KEY(day, path));
                CREATE TABLE analytics_referrers (day TEXT NOT NULL, host TEXT NOT NULL, views INTEGER NOT NULL, PRIMARY KEY(day, host));
                CREATE TABLE analytics_visitors (day TEXT NOT NULL, hash TEXT NOT NULL, PRIMARY KEY(day, hash));
                CREATE TABLE analytics_days (day TEXT PRIMARY KEY, visitors INTEGER NOT NULL);
                PRAGMA user_version=3;
                """;
            migration.ExecuteNonQuery();
        }
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT value FROM settings WHERE key='content_imported'";
        if (check.ExecuteScalar() is null)
        {
            if (importDirectory is not null && Directory.Exists(importDirectory))
                Import(connection, transaction, importDirectory);
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO settings(key,value) VALUES('content_imported','1')";
            mark.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void Import(SqliteConnection connection, SqliteTransaction transaction, string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var slug = Path.GetFileNameWithoutExtension(path);
            var metadata = JsonSerializer.Deserialize<PostMetadata>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (metadata is null || !Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$")
                || string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(metadata.Description)
                || metadata.Date == default || metadata.Tags is null || metadata.Tags.Length == 0
                || metadata.Tags.Any(tag => tag is null || !Regex.IsMatch(tag, "^[a-z0-9]+(?:-[a-z0-9]+)*$")))
                throw new InvalidDataException($"Invalid post metadata: {path}");
            var markdownPath = Path.ChangeExtension(path, ".md");
            if (!File.Exists(markdownPath)) throw new InvalidDataException($"Missing Markdown: {markdownPath}");
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO posts(id,slug,title,description,markdown,tags,featured,published_at,created_at,updated_at)
                VALUES($id,$slug,$title,$description,$markdown,$tags,$featured,$published,$created,$created)
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$slug", slug);
            insert.Parameters.AddWithValue("$title", metadata.Title);
            insert.Parameters.AddWithValue("$description", metadata.Description);
            insert.Parameters.AddWithValue("$markdown", File.ReadAllText(markdownPath));
            insert.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(metadata.Tags.Distinct().ToArray()));
            insert.Parameters.AddWithValue("$featured", metadata.Featured ? 1 : 0);
            insert.Parameters.AddWithValue("$published", metadata.Draft ? DBNull.Value : Timestamp(metadata.Date));
            insert.Parameters.AddWithValue("$created", Timestamp(metadata.Date));
            insert.ExecuteNonQuery();
        }
    }

    public static string Timestamp(DateTimeOffset date) => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
