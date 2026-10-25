using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace SevenBlog.Services;

public sealed record BlogUser(string Id, string Username, string DisplayName, string Bio, string Rank,
    string PasswordHash, string SecurityStamp, DateTimeOffset CreatedAt, bool Disabled,
    string? AvatarId = null, string? BannerId = null)
{
    public const string DefaultAvatarUrl = "/img/avatar.svg";
    public string ProfileUrl => $"/users/{Username}/";
    public string RankLabel => Rank == "Admin" ? "Administrator" : "Author";
    public string AvatarUrl => AvatarId is null ? DefaultAvatarUrl : MediaStore.Url(AvatarId);
    public string? BannerUrl => BannerId is null ? null : MediaStore.Url(BannerId);
}

public sealed class UserStore(BlogDatabase database, IPasswordHasher<BlogUser> hasher)
{
    public const string StampClaim = "7blog:stamp";
    private const string Columns = "id,username,display_name,bio,rank,password_hash,security_stamp,created_at,disabled,avatar_id,banner_id";
    private static readonly BlogUser DummyUser = new("", "", "", "", "Author", "", "", DateTimeOffset.MinValue, false);
    private readonly string dummyHash = hasher.HashPassword(DummyUser, Guid.NewGuid().ToString("N"));

    public BlogUser? FindByUsername(string username) => Find("username", username.Trim().ToLowerInvariant());
    public BlogUser? FindById(string id) => Find("id", id);
    private BlogUser? Find(string column, string value)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM users WHERE {column}=$value";
        command.Parameters.AddWithValue("$value", value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<BlogUser> List()
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM users ORDER BY username";
        using var reader = command.ExecuteReader(); var users = new List<BlogUser>();
        while (reader.Read()) users.Add(Read(reader));
        return users;
    }

    public BlogUser Create(string username, string password, string rank = "Author", string? displayName = null, bool claimLegacy = false)
    {
        username = NormalizeUsername(username);
        ValidatePassword(password);
        rank = NormalizeRank(rank);
        displayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim();
        if (displayName.Length > 80) throw new ArgumentException("Display names may contain at most 80 characters.");
        var user = new BlogUser(Guid.NewGuid().ToString("N"), username, displayName, "", rank, "",
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, false);
        var hash = hasher.HashPassword(user, password);
        using var connection = database.Open(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO users(id,username,display_name,rank,password_hash,security_stamp,created_at) VALUES($id,$username,$name,$rank,$hash,$stamp,$created)";
        command.Parameters.AddWithValue("$id", user.Id); command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$name", user.DisplayName); command.Parameters.AddWithValue("$rank", rank);
        command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$stamp", user.SecurityStamp);
        command.Parameters.AddWithValue("$created", BlogDatabase.Timestamp(user.CreatedAt));
        try { command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new ArgumentException("That username already exists."); }
        if (claimLegacy)
        {
            using var claim = connection.CreateCommand(); claim.Transaction = transaction;
            claim.CommandText = "UPDATE posts SET author_id=$id,version=version+1 WHERE author_id IS NULL";
            claim.Parameters.AddWithValue("$id", user.Id); claim.ExecuteNonQuery();
        }
        transaction.Commit();
        return user with { PasswordHash = hash };
    }

    public BlogUser? Authenticate(string username, string password)
    {
        var user = FindByUsername(username);
        if (user is null) { hasher.VerifyHashedPassword(DummyUser, dummyHash, password); return null; }
        using var connection = database.Open();
        // Serialize login attempts so concurrent requests cannot bypass account lockout.
        using var transaction = connection.BeginTransaction();
        using var state = connection.CreateCommand(); state.Transaction = transaction;
        state.CommandText = "SELECT failed_attempts,lockout_until,disabled,password_hash,security_stamp,rank FROM users WHERE id=$id";
        state.Parameters.AddWithValue("$id", user.Id);
        int failures; DateTimeOffset? locked; string currentHash; string currentStamp; string currentRank; bool disabled;
        using (var reader = state.ExecuteReader())
        {
            if (!reader.Read()) return null;
            failures = reader.GetInt32(0); locked = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1));
            disabled = reader.GetBoolean(2); currentHash = reader.GetString(3); currentStamp = reader.GetString(4);
            currentRank = reader.GetString(5);
        }
        var verification = hasher.VerifyHashedPassword(user, currentHash, password);
        if (disabled || locked > DateTimeOffset.UtcNow) return null;
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.Parameters.AddWithValue("$id", user.Id);
        if (verification == PasswordVerificationResult.Failed)
        {
            failures = locked.HasValue ? 1 : failures + 1;
            update.CommandText = "UPDATE users SET failed_attempts=$failures,lockout_until=$locked WHERE id=$id";
            update.Parameters.AddWithValue("$failures", failures);
            update.Parameters.AddWithValue("$locked", failures >= 5 ? BlogDatabase.Timestamp(DateTimeOffset.UtcNow.AddMinutes(10)) : DBNull.Value);
            update.ExecuteNonQuery(); transaction.Commit(); return null;
        }
        update.CommandText = "UPDATE users SET failed_attempts=0,lockout_until=NULL,password_hash=$hash WHERE id=$id";
        var hash = verification == PasswordVerificationResult.SuccessRehashNeeded ? hasher.HashPassword(user, password) : currentHash;
        update.Parameters.AddWithValue("$hash", hash); update.ExecuteNonQuery(); transaction.Commit();
        return user with { PasswordHash = hash, SecurityStamp = currentStamp, Rank = currentRank };
    }

    public bool VerifyPassword(BlogUser user, string password) => hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
    public void SetPassword(BlogUser user, string password)
    {
        ValidatePassword(password);
        ExecuteUpdate("password_hash=$value,security_stamp=$stamp,failed_attempts=0,lockout_until=NULL", user.Id, hasher.HashPassword(user, password));
    }
    // Profile URLs use the username, so links to the old /users/<name>/ address stop working.
    public BlogUser ChangeUsername(BlogUser user, string username)
    {
        username = NormalizeUsername(username);
        if (username == user.Username) return user;
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET username=$username WHERE id=$id";
        command.Parameters.AddWithValue("$username", username); command.Parameters.AddWithValue("$id", user.Id);
        try { command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new ArgumentException("That username is already taken."); }
        return user with { Username = username };
    }
    public int CountActiveAdmins()
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users WHERE rank='Admin' AND disabled=0";
        return Convert.ToInt32(command.ExecuteScalar());
    }
    public void SetRank(BlogUser user, string rank) => ExecuteUpdate("rank=$value,security_stamp=$stamp", user.Id, NormalizeRank(rank));
    public void SetDisabled(BlogUser user, bool disabled) => ExecuteUpdate("disabled=$value,security_stamp=$stamp", user.Id, disabled ? 1 : 0);
    public void UpdateProfile(BlogUser user, string name, string bio)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || bio.Length > 1000) throw new ArgumentException("Invalid profile details.");
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET display_name=$name,bio=$bio WHERE id=$id";
        command.Parameters.AddWithValue("$name", name.Trim()); command.Parameters.AddWithValue("$bio", bio.Trim());
        command.Parameters.AddWithValue("$id", user.Id); command.ExecuteNonQuery();
    }
    // Imported posts start without an owner, so nobody can edit them and bylines fall back to
    // Blog:Author. The oldest active administrator takes them over. Returns the number claimed.
    public int ClaimUnownedPosts()
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE posts SET author_id=(SELECT id FROM users WHERE rank='Admin' AND disabled=0 ORDER BY created_at, username LIMIT 1),
                version=version+1
            WHERE author_id IS NULL AND EXISTS (SELECT 1 FROM users WHERE rank='Admin' AND disabled=0)
            """;
        return command.ExecuteNonQuery();
    }

    // Returns the previous image id so the caller can delete its file.
    public string? SetAvatar(BlogUser user, string? mediaId) => SetPicture("avatar_id", user.Id, mediaId);
    public string? SetBanner(BlogUser user, string? mediaId) => SetPicture("banner_id", user.Id, mediaId);
    private string? SetPicture(string column, string id, string? mediaId)
    {
        using var connection = database.Open(); using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = $"SELECT {column} FROM users WHERE id=$id";
        read.Parameters.AddWithValue("$id", id);
        var previous = read.ExecuteScalar() as string;
        using var write = connection.CreateCommand(); write.Transaction = transaction;
        write.CommandText = $"UPDATE users SET {column}=$value WHERE id=$id";
        write.Parameters.AddWithValue("$id", id); write.Parameters.AddWithValue("$value", (object?)mediaId ?? DBNull.Value);
        write.ExecuteNonQuery(); transaction.Commit();
        return previous;
    }
    private void ExecuteUpdate(string assignments, string id, object value)
    {
        using var connection = database.Open(); using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE users SET {assignments} WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString("N")); command.ExecuteNonQuery();
    }
    public static string NormalizeUsername(string username)
    {
        username = username.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(username, "^[a-z0-9][a-z0-9_-]{2,31}$"))
            throw new ArgumentException("Usernames need 3–32 lowercase letters, digits, hyphens or underscores, starting with a letter or digit.");
        return username;
    }
    public static void ValidatePassword(string password)
    {
        if (password.Length is < 12 or > 128) throw new ArgumentException("Passwords must contain 12–128 characters.");
    }
    public static string NormalizeRank(string rank) => rank.ToLowerInvariant() switch {
        "admin" or "administrator" => "Admin", "author" => "Author", _ => throw new ArgumentException("Rank must be Admin or Author.")
    };
    public static string? UserId(ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier);
    private static BlogUser Read(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)), reader.GetBoolean(8),
        reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10));
}
