using System.Text.RegularExpressions;

namespace SevenBlog.Services;

public sealed record MediaItem(string Id, string OwnerId, string? PostId, string Kind, int Width, int Height, long Bytes, DateTimeOffset CreatedAt)
{
    public string Url => MediaStore.Url(Id);
}

// Uploaded images live as <data>/media/<id>.webp, with one row per file in the media table.
public sealed partial class MediaStore(BlogDatabase database)
{
    public const string Attachment = "attachment", PostBanner = "post-banner", Avatar = "avatar", ProfileBanner = "profile-banner",
        SiteIcon = "site-icon", SiteHeader = "site-header";
    public const int UploadsPerHour = 60;
    private const string Columns = "id,owner_id,post_id,kind,width,height,bytes,created_at";
    public string DirectoryPath => Path.Combine(database.DirectoryPath, "media");
    public static string Url(string id) => $"/media/{id}.webp";
    public static bool IsFileName(string name) => FileName().IsMatch(name);
    public string FilePath(string id) => Path.Combine(DirectoryPath, id + ".webp");

    public MediaItem Save(string ownerId, string kind, ProcessedImage image, string? postId = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        var item = new MediaItem(Guid.NewGuid().ToString("N"), ownerId, postId, kind, image.Width, image.Height, image.Bytes.Length, DateTimeOffset.UtcNow);
        var temporary = FilePath(item.Id) + ".tmp";
        File.WriteAllBytes(temporary, image.Bytes);
        File.Move(temporary, FilePath(item.Id));
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO media({Columns}) VALUES($id,$owner,$post,$kind,$width,$height,$bytes,$created)";
        command.Parameters.AddWithValue("$id", item.Id); command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$post", (object?)postId ?? DBNull.Value); command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$width", image.Width); command.Parameters.AddWithValue("$height", image.Height);
        command.Parameters.AddWithValue("$bytes", image.Bytes.Length); command.Parameters.AddWithValue("$created", BlogDatabase.Timestamp(item.CreatedAt));
        command.ExecuteNonQuery();
        return item;
    }

    public int CountRecent(string ownerId, TimeSpan window)
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM media WHERE owner_id=$owner AND created_at>=$since";
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$since", BlogDatabase.Timestamp(DateTimeOffset.UtcNow - window));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // Attachments already linked to the post, plus the author's own uploads its text refers to.
    public IReadOnlyList<MediaItem> ForEditor(string ownerId, string? postId, string? markdown)
    {
        var referenced = References(markdown);
        var (filter, parameters) = InList(referenced);
        return Query($"WHERE owner_id=$owner AND kind='attachment' AND (post_id=$post OR id IN ({filter})) ORDER BY created_at",
            [("$owner", ownerId), ("$post", (object?)postId ?? DBNull.Value), .. parameters]);
    }

    // Uploads made while writing a new post are claimed when the post is saved.
    public void LinkReferenced(string ownerId, string postId, string markdown)
    {
        var referenced = References(markdown);
        if (referenced.Count == 0) return;
        var (filter, parameters) = InList(referenced);
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE media SET post_id=$post WHERE owner_id=$owner AND post_id IS NULL AND kind='attachment' AND id IN ({filter})";
        command.Parameters.AddWithValue("$post", postId); command.Parameters.AddWithValue("$owner", ownerId);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    // When an administrator gives a post to another author, its images move with it.
    public void TransferPost(string postId, string ownerId)
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE media SET owner_id=$owner WHERE post_id=$post";
        command.Parameters.AddWithValue("$owner", ownerId); command.Parameters.AddWithValue("$post", postId);
        command.ExecuteNonQuery();
    }

    public (int Count, long Bytes) Totals()
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(bytes),0) FROM media";
        using var reader = command.ExecuteReader(); reader.Read();
        return (reader.GetInt32(0), reader.GetInt64(1));
    }

    public void DeleteForPost(string postId)
    {
        foreach (var item in Query("WHERE post_id=$post", [("$post", postId)])) Delete(item.Id);
    }

    // Removes attachments that were uploaded but never saved into a post.
    public int DeleteAbandoned(TimeSpan age)
    {
        var abandoned = Query("WHERE post_id IS NULL AND kind='attachment' AND created_at<$before",
            [("$before", BlogDatabase.Timestamp(DateTimeOffset.UtcNow - age))]);
        foreach (var item in abandoned) Delete(item.Id);
        return abandoned.Count;
    }

    public void Delete(string? id)
    {
        if (id is null) return;
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM media WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        File.Delete(FilePath(id));
    }

    public static IReadOnlyList<string> References(string? markdown) => markdown is null ? [] :
        Reference().Matches(markdown).Select(match => match.Groups[1].Value).Distinct().Take(200).ToArray();

    private static (string Filter, (string, object)[] Parameters) InList(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return ("NULL", []);
        var parameters = ids.Select((id, index) => ($"$ref{index}", (object)id)).ToArray();
        return (string.Join(",", parameters.Select(parameter => parameter.Item1)), parameters);
    }

    private IReadOnlyList<MediaItem> Query(string suffix, (string Name, object Value)[] parameters)
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM media {suffix}";
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader(); var items = new List<MediaItem>();
        while (reader.Read()) items.Add(new MediaItem(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6), DateTimeOffset.Parse(reader.GetString(7))));
        return items;
    }

    [GeneratedRegex(@"/media/([a-f0-9]{32})\.webp")] private static partial Regex Reference();
    [GeneratedRegex(@"^[a-f0-9]{32}\.webp$")] private static partial Regex FileName();
}

public sealed class MediaCleanup(MediaStore media, ILogger<MediaCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                var removed = media.DeleteAbandoned(TimeSpan.FromDays(1));
                if (removed > 0) logger.LogInformation("Removed {Count} unused image uploads.", removed);
            }
            catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
            { logger.LogWarning(error, "Image cleanup failed; it will be retried."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
