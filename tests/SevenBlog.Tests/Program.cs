using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SevenBlog.Services;
using SkiaSharp;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var dotnet = Path.Combine(root, ".tools", "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
if (!File.Exists(dotnet)) dotnet = "dotnet";
var temporary = Path.Combine(Path.GetTempPath(), "7blog-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var passed = 0;
const string password = "7blog-Test-Password!2026";
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS {message}"); passed++;
}
IPasswordHasher<BlogUser> Hasher() => new PasswordHasher<BlogUser>(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
PostStore Posts(BlogDatabase database) => new(database, new SiteSettingsStore(database, Options.Create(new BlogOptions())));
PostWrite Write(string slug, bool publish = false, string markdown = "## A heading\n\nA test post.") =>
    new("Test post", "A test description.", slug, markdown, ["test"], publish);
byte[] Image(int width, int height, SKEncodedImageFormat format)
{
    using var bitmap = new SKBitmap(width, height);
    using (var canvas = new SKCanvas(bitmap))
    using (var paint = new SKPaint { Color = SKColors.Orange })
    {
        canvas.Clear(SKColors.SteelBlue);
        canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
    }
    using var data = bitmap.Encode(format, 90);
    return data.ToArray();
}
bool IsWebp(byte[] bytes) => bytes.Length > 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);

ProcessStartInfo StartInfo(params string[] arguments)
{
    var info = new ProcessStartInfo(dotnet) { WorkingDirectory = root, UseShellExecute = false };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    info.Environment["Blog__DataPath"] = Path.Combine(temporary, "runtime");
    info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    info.Environment["AllowedHosts"] = "*";
    return info;
}
async Task<(int ExitCode, string Output)> Cli(params string[] arguments)
{
    var info = StartInfo([Path.Combine(root, "bin", configuration, "net10.0", "SevenBlog.dll"), "users", .. arguments]);
    info.RedirectStandardInput = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
    using var child = Process.Start(info)!;
    await child.StandardInput.WriteLineAsync(password); child.StandardInput.Close();
    var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    return (child.ExitCode, (await output) + (await error));
}

try
{
    var unitDatabase = new BlogDatabase(Path.Combine(temporary, "unit"));
    var users = new UserStore(unitDatabase, Hasher()); var posts = Posts(unitDatabase);
    var author = users.Create("author", password); var other = users.Create("other", password);
    Check(author.PasswordHash != password && users.VerifyPassword(author, password), "Passwords use framework hashing");
    Check(users.Authenticate("author", password)?.Id == author.Id, "Correct credentials authenticate");
    Check(users.Authenticate("missing", password) is null, "Unknown users cannot authenticate");
    var draft = posts.Save(author.Id, null, 0, Write("private-draft"));
    Check(posts.Published().Count == 0, "Drafts are absent from public queries");
    Check(posts.FindOwned(draft.Id, other.Id) is null, "Other authors cannot read private posts");
    var rejected = false;
    try { posts.Save(other.Id, draft.Id, draft.Version, Write("stolen", true)); } catch (KeyNotFoundException) { rejected = true; }
    Check(rejected, "Ownership is enforced in the post repository");
    Check(!posts.DeleteOwned(draft.Id, other.Id, draft.Version), "Other authors cannot delete posts");
    var published = posts.Save(author.Id, draft.Id, draft.Version, Write("private-draft", true));
    Check(posts.Published().Single().AuthorId == author.Id, "Published posts retain their author");
    rejected = false;
    try { posts.Save(author.Id, draft.Id, draft.Version, Write("private-draft", true)); } catch (PostConflictException) { rejected = true; }
    Check(rejected, "Stale edits cannot overwrite a newer version");
    Check(!posts.DeleteOwned(draft.Id, author.Id, draft.Version), "Stale delete confirmation cannot remove a newer version");
    var reopened = new BlogDatabase(Path.Combine(temporary, "unit"));
    Check(Posts(reopened).Published().Single().Title == published.Title, "Posts persist when the database is reopened");
    Check(new UserStore(reopened, Hasher()).FindByUsername("AUTHOR")?.Id == author.Id, "Accounts persist and username lookup ignores case");
    var locked = users.Create("locked", password);
    for (var i = 0; i < 5; i++) users.Authenticate(locked.Username, "incorrect-password");
    Check(users.Authenticate(locked.Username, password) is null, "Repeated failed passwords lock the account");
    users.SetPassword(locked, password);
    Check(users.Authenticate(locked.Username, password) is not null, "Administrator password reset clears lockout");
    var html = MarkdownRenderer.Render("<script>alert(1)</script>\n\n[unsafe](javascript:alert(1))\n\n[archive](/archive/)\n\n**formatted**");
    Check(!html.Contains("<script>") && !html.Contains("href=\"javascript:"), "Markdown rejects raw scripts and unsafe link schemes");
    Check(html.Contains("href=\"/archive/\"") && html.Contains("<strong>formatted</strong>"), "Safe Markdown links and formatting still render");
    Check(MarkdownRenderer.Render("![photo](/media/x.webp)").Contains("loading=\"lazy\""), "Post images load lazily");
    var renamedAuthor = users.ChangeUsername(author, " Author-2 ");
    Check(renamedAuthor.Username == "author-2" && users.FindByUsername("author-2")?.Id == author.Id && users.FindByUsername("author") is null,
        "Usernames can be changed and are normalised to lowercase");
    foreach (var invalidName in new[] { "other", "OTHER", "ab", "-starts-with-hyphen", "has space" })
    {
        rejected = false;
        try { users.ChangeUsername(renamedAuthor, invalidName); } catch (ArgumentException) { rejected = true; }
        Check(rejected && users.FindById(author.Id)!.Username == "author-2", $"Username change to '{invalidName}' is refused");
    }
    users.ChangeUsername(renamedAuthor, "author");

    var photo = Image(4000, 3000, SKEncodedImageFormat.Jpeg);
    var attachment = ImageProcessor.Process(photo, ImageKind.Attachment);
    Check(IsWebp(attachment.Bytes) && attachment.Width == 1600 && attachment.Height == 1200 && attachment.Bytes.Length < photo.Length,
        "Attachments are resized to 1600 px and re-encoded as smaller WebP");
    var small = ImageProcessor.Process(Image(300, 200, SKEncodedImageFormat.Png), ImageKind.Attachment);
    Check(small.Width == 300 && small.Height == 200, "Small images are never upscaled");
    var avatar = ImageProcessor.Process(photo, ImageKind.Avatar);
    var postBanner = ImageProcessor.Process(Image(1000, 2000, SKEncodedImageFormat.Png), ImageKind.PostBanner);
    Check(avatar.Width == 256 && avatar.Height == 256 && postBanner.Width == 1000 && postBanner.Height == 375,
        "Avatars are cropped square and banners to 8:3");
    using (var decoded = SKBitmap.Decode(attachment.Bytes))
        Check(decoded?.Width == 1600, "Compressed images decode correctly");
    rejected = false;
    try { ImageProcessor.Process("not an image"u8.ToArray(), ImageKind.Attachment); } catch (ImageUploadException) { rejected = true; }
    Check(rejected, "Non-image uploads are rejected");

    var media = new MediaStore(unitDatabase);
    var stored = media.Save(author.Id, MediaStore.Attachment, small);
    Check(File.Exists(media.FilePath(stored.Id)) && media.ForEditor(author.Id, null, $"![x]({stored.Url})").Single().Id == stored.Id,
        "Uploaded images are stored and found from the post text");
    media.LinkReferenced(other.Id, draft.Id, $"![x]({stored.Url})");
    Check(media.ForEditor(author.Id, draft.Id, "").Count == 0, "Other authors cannot claim someone else's upload");
    media.LinkReferenced(author.Id, draft.Id, $"![x]({stored.Url})");
    Check(media.ForEditor(author.Id, draft.Id, "").Single().Id == stored.Id, "Saving a post claims the attachments it uses");
    media.DeleteForPost(draft.Id);
    Check(!File.Exists(media.FilePath(stored.Id)), "Deleting a post's images removes their files");
    var abandoned = media.Save(author.Id, MediaStore.Attachment, small);
    using (var connection = unitDatabase.Open())
    using (var age = connection.CreateCommand())
    {
        age.CommandText = "UPDATE media SET created_at='2000-01-01T00:00:00.0000000+00:00' WHERE id=$id";
        age.Parameters.AddWithValue("$id", abandoned.Id); age.ExecuteNonQuery();
    }
    Check(media.DeleteAbandoned(TimeSpan.FromDays(1)) == 1 && !File.Exists(media.FilePath(abandoned.Id)), "Unused uploads are cleaned up after a day");

    // A database created before images existed (schema version 1) is migrated in place.
    var versionOne = Path.Combine(temporary, "version-one");
    Directory.CreateDirectory(versionOne);
    using (var connection = new SqliteConnection($"Data Source={Path.Combine(versionOne, "7blog.db")};Pooling=False"))
    {
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE users (id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE COLLATE NOCASE, display_name TEXT NOT NULL,
                bio TEXT NOT NULL DEFAULT '', rank TEXT NOT NULL CHECK(rank IN ('Admin','Author')), password_hash TEXT NOT NULL,
                security_stamp TEXT NOT NULL, created_at TEXT NOT NULL, failed_attempts INTEGER NOT NULL DEFAULT 0, lockout_until TEXT,
                disabled INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE posts (id TEXT PRIMARY KEY, slug TEXT NOT NULL UNIQUE, author_id TEXT REFERENCES users(id), title TEXT NOT NULL,
                description TEXT NOT NULL, markdown TEXT NOT NULL, tags TEXT NOT NULL, featured INTEGER NOT NULL DEFAULT 0, published_at TEXT,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO settings VALUES('content_imported','1');
            PRAGMA user_version=1;
            """;
        schema.ExecuteNonQuery();
    }
    var migrated = new BlogDatabase(versionOne);
    var migratedUser = new UserStore(migrated, Hasher()).Create("migrated", password);
    var migratedPost = Posts(migrated).Save(migratedUser.Id, null, 0, Write("migrated-post", true));
    new UserStore(migrated, Hasher()).SetAvatar(migratedUser, new MediaStore(migrated).Save(migratedUser.Id, MediaStore.Avatar, avatar).Id);
    Check(new UserStore(migrated, Hasher()).FindById(migratedUser.Id)!.AvatarId is not null && migratedPost.BannerId is null,
        "Version 1 databases gain picture and banner columns");

    // A version 2 database (with images, before site images and analytics) keeps its rows when upgraded.
    var versionTwo = Path.Combine(temporary, "version-two");
    Directory.CreateDirectory(versionTwo);
    using (var connection = new SqliteConnection($"Data Source={Path.Combine(versionTwo, "7blog.db")};Pooling=False"))
    {
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE users (id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE COLLATE NOCASE, display_name TEXT NOT NULL,
                bio TEXT NOT NULL DEFAULT '', rank TEXT NOT NULL CHECK(rank IN ('Admin','Author')), password_hash TEXT NOT NULL,
                security_stamp TEXT NOT NULL, created_at TEXT NOT NULL, failed_attempts INTEGER NOT NULL DEFAULT 0, lockout_until TEXT,
                disabled INTEGER NOT NULL DEFAULT 0, avatar_id TEXT, banner_id TEXT);
            CREATE TABLE posts (id TEXT PRIMARY KEY, slug TEXT NOT NULL UNIQUE, author_id TEXT REFERENCES users(id), title TEXT NOT NULL,
                description TEXT NOT NULL, markdown TEXT NOT NULL, tags TEXT NOT NULL, featured INTEGER NOT NULL DEFAULT 0, published_at TEXT,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1, banner_id TEXT);
            CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE media (id TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES users(id), post_id TEXT,
                kind TEXT NOT NULL CHECK(kind IN ('attachment','post-banner','avatar','profile-banner')),
                width INTEGER NOT NULL, height INTEGER NOT NULL, bytes INTEGER NOT NULL, created_at TEXT NOT NULL);
            CREATE INDEX ix_media_owner ON media(owner_id, created_at);
            CREATE INDEX ix_media_post ON media(post_id);
            INSERT INTO settings VALUES('content_imported','1');
            INSERT INTO users(id,username,display_name,rank,password_hash,security_stamp,created_at,avatar_id)
                VALUES('u1','old','Old user','Admin','x','s','2026-01-01T00:00:00.0000000+00:00','00000000000000000000000000000001');
            INSERT INTO media VALUES('00000000000000000000000000000001','u1',NULL,'avatar',256,256,1000,'2026-01-01T00:00:00.0000000+00:00');
            PRAGMA user_version=2;
            """;
        schema.ExecuteNonQuery();
    }
    var upgraded = new BlogDatabase(versionTwo);
    var upgradedMedia = new MediaStore(upgraded);
    Check(upgradedMedia.Totals().Count == 1 && new UserStore(upgraded, Hasher()).FindByUsername("old")!.AvatarId == "00000000000000000000000000000001",
        "Version 2 databases keep their images when upgraded");
    upgradedMedia.Save("u1", MediaStore.SiteIcon, small);
    Check(upgradedMedia.Totals().Count == 2, "Upgraded databases accept site images");

    // Site settings fall back to configuration until an administrator saves them.
    var configured = new BlogOptions { Name = "Configured", Description = "From config", Author = "Config Author" };
    var site = new SiteSettingsStore(upgraded, Options.Create(configured));
    Check(site.Current is { Name: "Configured", Description: "From config", Author: "Config Author", IconId: null } && site.Current.IconUrl == "/favicon.svg",
        "Site settings default to configuration");
    site.Save(site.Current with { Name = "  Saved name ", About = "## Hello", IconId = "00000000000000000000000000000001" });
    var reloaded = new SiteSettingsStore(upgraded, Options.Create(configured)).Current;
    Check(reloaded is { Name: "Saved name", About: "## Hello", Description: "From config" } && reloaded.IconUrl == "/media/00000000000000000000000000000001.webp",
        "Saved site settings override configuration and persist");
    Check(new PostStore(upgraded, site).Save("u1", null, 0, Write("site-post", true)).AuthorName == "Old user", "Posts still show their own author");
    foreach (var invalid in new[] { site.Current with { Name = " " }, site.Current with { Author = "" }, site.Current with { Name = new string('x', 61) } })
    {
        rejected = false;
        try { site.Save(invalid); } catch (ArgumentException) { rejected = true; }
        Check(rejected && site.Current.Name == "Saved name", "Invalid site settings are refused");
    }

    // Analytics: only real public page views count, and visitors are deduplicated per day.
    var analytics = new AnalyticsCollector(upgraded, Options.Create(new BlogOptions { SiteUrl = "https://blog.example.com" }));
    Microsoft.AspNetCore.Http.DefaultHttpContext Visit(string path, string agent = "Mozilla/5.0 Firefox/140.0", string? referrer = null,
        string ip = "203.0.113.5", bool signedIn = false, int status = 200, string type = "text/html; charset=utf-8", string? dnt = null)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = "GET"; context.Request.Path = path; context.Request.Host = new("blog.example.com");
        context.Request.Headers.UserAgent = agent;
        if (referrer is not null) context.Request.Headers.Referer = referrer;
        if (dnt is not null) context.Request.Headers["DNT"] = dnt;
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Response.StatusCode = status; context.Response.ContentType = type;
        if (signedIn) context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([], "test"));
        return context;
    }
    Check(AnalyticsCollector.ShouldCount(Visit("/")), "Public page views are counted");
    foreach (var (context, reason) in new[] {
        (Visit("/", agent: "Googlebot/2.1"), "bots"), (Visit("/", agent: ""), "requests without a user agent"),
        (Visit("/", signedIn: true), "signed-in users"), (Visit("/admin/"), "admin pages"), (Visit("/manage/"), "editor pages"),
        (Visit("/missing/", status: 404), "error pages"), (Visit("/rss.xml", type: "application/rss+xml"), "feeds") })
        Check(!AnalyticsCollector.ShouldCount(context), $"Analytics ignore {reason}");
    foreach (var context in new[] {
        Visit("/"), Visit("/Posts/Hello-World"), Visit("/posts/hello-world/", referrer: "https://news.example/item?id=1"),
        Visit("/posts/hello-world/", referrer: "https://blog.example.com/"), Visit("/", ip: "198.51.100.7", dnt: "1") })
        analytics.Record(context);
    var report = analytics.Report(7);
    Check(report.Views == 5 && report.Days.First().Views == 5 && report.Days.Count == 7, "Page views are totalled per day");
    Check(report.Visitors == 1, "Repeat visits count one visitor, and Do Not Track visits are not counted as visitors");
    Check(report.Pages.First() is { Key: "/posts/hello-world/", Views: 3 }, "Paths are normalised before counting");
    Check(report.Referrers.Single() is { Key: "news.example", Views: 1 }, "Only other sites are recorded as referrers, by host name only");
    using (var connection = upgraded.Open())
    using (var hashQuery = connection.CreateCommand())
    {
        hashQuery.CommandText = "SELECT group_concat(hash) FROM analytics_visitors";
        var hashes = hashQuery.ExecuteScalar() as string ?? "";
        Check(!hashes.Contains("203.0.113.5") && hashes.Length == 32, "Visitor records hold a salted hash, never the IP address");
    }

    var legacy = Path.Combine(temporary, "legacy");
    Directory.CreateDirectory(legacy);
    foreach (var slug in new[] { "hello-world", "own-your-files", "sample-three", "sample-four", "sample-five", "sample-six" })
    {
        File.WriteAllText(Path.Combine(legacy, slug + ".json"), System.Text.Json.JsonSerializer.Serialize(new {
            title = slug == "own-your-files" ? "Your words deserve ordinary files" : slug,
            description = "Test article description", date = "2020-01-01T12:00:00Z", tags = new[] { "small-web" }
        }));
        File.WriteAllText(Path.Combine(legacy, slug + ".md"), "## A heading\n\nSome test content.");
    }
    File.WriteAllText(Path.Combine(legacy, "scheduled.json"), """{"title":"Scheduled","description":"Private until its date","date":"2999-01-01T00:00:00Z","tags":["test"]}""");
    File.WriteAllText(Path.Combine(legacy, "scheduled.md"), "Scheduled content.");
    var runtime = new BlogDatabase(Path.Combine(temporary, "runtime"), legacy);
    var runtimeUsers = new UserStore(runtime, Hasher()); var runtimePosts = Posts(runtime);
    var alice = runtimeUsers.Create("alice", password, "Admin", "Alice", claimLegacy: true);
    Check(runtimePosts.Published().Count >= 6 && runtimePosts.Published().All(post => post.AuthorId == alice.Id), "Initial Markdown import assigns legacy posts when explicitly requested");
    var claimDatabase = new BlogDatabase(Path.Combine(temporary, "claim"), legacy);
    var claimUsers = new UserStore(claimDatabase, Hasher());
    claimUsers.Create("writer", password);
    Check(claimUsers.ClaimUnownedPosts() == 0 && Posts(claimDatabase).Published().All(post => post.AuthorId is null && post.AuthorName == new BlogOptions().Author),
        "Imported posts stay unowned while there is no administrator");
    var owner = claimUsers.Create("owner", password, "Admin", "Olivia");
    Check(claimUsers.ClaimUnownedPosts() >= 6 && Posts(claimDatabase).Published().All(post => post.AuthorId == owner.Id && post.AuthorName == "Olivia"),
        "Unowned posts are credited to the first administrator, not the placeholder author");
    Check(runtimePosts.Published().All(post => post.Slug != "scheduled"), "Future-dated imports stay out of public queries");
    var cli = await Cli("create", "bob", "--display-name", "Bob");
    Check(cli.ExitCode == 0 && runtimeUsers.FindByUsername("bob") is not null, "The account CLI creates users from standard input");
    cli = await Cli("create", "bob");
    Check(cli.ExitCode != 0, "Duplicate CLI registration fails");
    cli = await Cli("list");
    Check(cli.ExitCode == 0 && cli.Output.Contains("alice") && cli.Output.Contains("bob") && !cli.Output.Contains(password), "CLI listing shows accounts without exposing passwords");

    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var info = StartInfo("run", "--no-build", "--configuration", configuration, "--project", "SevenBlog.csproj", "--no-launch-profile", "--urls", $"http://127.0.0.1:{port}");
    info.Environment["Blog__SiteUrl"] = $"http://127.0.0.1:{port}";
    using var process = Process.Start(info)!;
    HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(8)
    };
    using var anonymous = Client(); using var aliceClient = Client(); using var bobClient = Client();
    async Task<string> Token(HttpClient client, string path)
    {
        var page = await client.GetStringAsync(path);
        var match = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success) throw new InvalidOperationException("No CSRF token at " + path);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    async Task<HttpResponseMessage> Multipart(HttpClient client, string path, string tokenPath, Dictionary<string, string> fields,
        (string Field, byte[] Bytes)[] files, string? requestedWith = null)
    {
        var content = new MultipartFormDataContent { { new StringContent(await Token(client, tokenPath)), "__RequestVerificationToken" } };
        foreach (var (name, value) in fields) content.Add(new StringContent(value), name);
        foreach (var (field, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new("image/png");
            content.Add(part, field, "holiday-photo.png");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (requestedWith is not null) request.Headers.Add("X-Requested-With", requestedWith);
        return await client.SendAsync(request);
    }
    async Task<HttpResponseMessage> Form(HttpClient client, string path, Dictionary<string, string> fields, string? tokenPath = null)
    {
        fields["__RequestVerificationToken"] = await Token(client, tokenPath ?? path.Split('?')[0]);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    async Task Login(HttpClient client, string username, string credential = password)
    {
        using var response = await Form(client, "/account/login/", new() {
            ["Input.Username"] = username, ["Input.Password"] = credential, ["ReturnUrl"] = "https://untrusted.example/"
        });
        Check(response.StatusCode == HttpStatusCode.Found && response.Headers.Location?.OriginalString == "/manage/", $"{username} signs in and external redirects are rejected");
        Check(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(cookie => cookie.Contains("7blog.auth=") && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)), "Authentication cookies are HTTP-only");
    }
    Dictionary<string, string> PostFields(string slug, string status = "Draft", int version = 0, string text = "## HTTP test\n\nA saved post.") => new() {
        ["Input.Title"] = "HTTP test post", ["Input.Description"] = "A post created through the editor.",
        ["Input.Slug"] = slug, ["Input.Tags"] = "test, projects", ["Input.Markdown"] = text,
        ["Input.Status"] = status, ["Input.Version"] = version.ToString()
    };

    try
    {
        var ready = false;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (process.HasExited) throw new InvalidOperationException("7blog exited during startup.");
            try { ready = (await anonymous.GetAsync("/healthz")).IsSuccessStatusCode; } catch (HttpRequestException) { }
            if (ready) break;
            await Task.Delay(100);
        }
        Check(ready, "The live server becomes healthy");
        foreach (var path in new[] { "/", "/archive/", "/about/", "/posts/hello-world/", "/tags/small-web/", "/search/?q=ordinary%20files", "/css/site.css", "/js/editor.js", "/vendor/7.css", "/users/alice/", "/account/login/" })
        {
            using var response = await anonymous.GetAsync(path);
            Check(response.IsSuccessStatusCode && response.Headers.Contains("Content-Security-Policy"), $"GET {path} works with security headers");
        }
        Check(!(await anonymous.GetStringAsync("/")).Contains("<script"), "Public pages need no JavaScript");
        using (var protectedPage = await anonymous.GetAsync("/manage/"))
            Check(protectedPage.StatusCode == HttpStatusCode.Found && protectedPage.Headers.Location!.OriginalString.Contains("/account/login"), "Author pages require authentication");
        await Login(aliceClient, "alice"); await Login(bobClient, "bob");
        var csrfFailure = await aliceClient.PostAsync("/manage/editor/?handler=Save", new FormUrlEncodedContent(PostFields("csrf-post")));
        Check(csrfFailure.StatusCode == HttpStatusCode.BadRequest, "Editor rejects missing CSRF tokens");
        using var created = await Form(aliceClient, "/manage/editor/?handler=Save", PostFields("http-private"));
        Check(created.StatusCode == HttpStatusCode.Found, "Editor saves a new draft");
        var id = Regex.Match(created.Headers.Location!.OriginalString, "editor/([a-f0-9]+)").Groups[1].Value;
        var editorPath = $"/manage/editor/{id}/";
        var saved = runtimePosts.FindOwned(id, alice.Id)!;
        Check(saved.Markdown.Contains("A saved post") && saved.AuthorId == alice.Id, "Server assigns post ownership from the signed-in session");
        using (var hidden = await anonymous.GetAsync("/posts/http-private/")) Check(hidden.StatusCode == HttpStatusCode.NotFound, "Draft URLs are private");
        Check(!(await anonymous.GetStringAsync("/rss.xml")).Contains("http-private"), "Drafts are excluded from RSS");
        Check(!(await anonymous.GetStringAsync("/users/alice/")).Contains("HTTP test post"), "Drafts are excluded from public profile posts");
        using (var forbidden = await bobClient.GetAsync(editorPath)) Check(forbidden.StatusCode == HttpStatusCode.NotFound, "Other authors cannot open an editor by guessing its URL");
        using (var forbidden = await Form(bobClient, editorPath + "?handler=Save", PostFields("stolen", "Published", saved.Version), "/manage/editor/"))
            Check(forbidden.StatusCode == HttpStatusCode.NotFound, "Forged edit submissions cannot change another author's post");
        using (var forbidden = await Form(bobClient, $"/manage/delete/{id}/", new() { ["Version"] = saved.Version.ToString() }, "/manage/editor/"))
            Check(forbidden.StatusCode == HttpStatusCode.NotFound, "Forged delete submissions cannot remove another author's post");
        var previewFields = PostFields("http-private", version: saved.Version, text: "**A preview**\n\n<script>alert(1)</script>");
        previewFields["__RequestVerificationToken"] = await Token(aliceClient, editorPath);
        using var previewRequest = new HttpRequestMessage(HttpMethod.Post, editorPath + "?handler=Preview") { Content = new FormUrlEncodedContent(previewFields) };
        previewRequest.Headers.Add("X-Requested-With", "7blog-preview");
        using var preview = await aliceClient.SendAsync(previewRequest);
        var previewHtml = await preview.Content.ReadAsStringAsync();
        Check(preview.IsSuccessStatusCode && previewHtml.Contains("<strong>A preview</strong>") && !previewHtml.Contains("<script>"), "Editor preview renders Markdown safely");
        Check(runtimePosts.FindOwned(id, alice.Id)!.Markdown == saved.Markdown, "Preview does not save or publish changes");
        using (var publish = await Form(aliceClient, editorPath + "?handler=Save", PostFields("http-private", "Published", saved.Version)))
            Check(publish.StatusCode == HttpStatusCode.Found, "Author can publish a draft");
        var current = runtimePosts.FindOwned(id, alice.Id)!;
        Check((await anonymous.GetStringAsync("/posts/http-private/")).Contains("Alice"), "Published articles show and link their actual author");
        Check((await anonymous.GetStringAsync("/users/alice/")).Contains("Administrator"), "Public profiles show the user's rank");
        Check(Regex.IsMatch(await anonymous.GetStringAsync("/users/alice/"), @"<dt>Published posts</dt>\s*<dd>7</dd>"), "Profiles count published posts without counting drafts");
        Check((await anonymous.GetStringAsync("/users/alice/")).Contains("HTTP test post"), "Public profiles list the user's published posts");
        using (var stale = await Form(aliceClient, editorPath + "?handler=Save", PostFields("http-private", "Published", saved.Version)))
            Check(stale.StatusCode == HttpStatusCode.Conflict, "HTTP editor detects stale concurrent edits");
        var invalid = PostFields("http-private", "Published", current.Version); invalid["Input.Title"] = "";
        using (var validation = await Form(aliceClient, editorPath + "?handler=Save", invalid))
            Check(validation.StatusCode == HttpStatusCode.OK && runtimePosts.FindOwned(id, alice.Id)!.Version == current.Version, "Validation errors preserve the saved post");

        var png = Image(2400, 1200, SKEncodedImageFormat.Png);
        string attachmentUrl;
        using (var upload = await Multipart(aliceClient, editorPath + "?handler=Upload", editorPath, [], [("Attachment", png)], "7blog-upload"))
        {
            var json = System.Text.Json.JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement;
            attachmentUrl = json.GetProperty("url").GetString()!;
            Check(upload.IsSuccessStatusCode && json.GetProperty("markdown").GetString() == $"![holiday photo]({attachmentUrl})" && json.GetProperty("width").GetInt32() == 1600,
                "Editor uploads return a compressed image and its Markdown line");
        }
        using (var image = await anonymous.GetAsync(attachmentUrl))
            Check(image.IsSuccessStatusCode && image.Content.Headers.ContentType?.MediaType == "image/webp"
                && image.Headers.CacheControl?.ToString().Contains("immutable") == true && IsWebp(await image.Content.ReadAsByteArrayAsync()),
                "Uploaded images are served as cacheable WebP");
        using (var notImage = await Multipart(aliceClient, editorPath + "?handler=Upload", editorPath, [], [("Attachment", "plain text"u8.ToArray())], "7blog-upload"))
            Check(notImage.StatusCode == HttpStatusCode.BadRequest, "The editor rejects files that are not images");
        using (var noJs = await Multipart(aliceClient, editorPath + "?handler=Upload", editorPath, PostFields("http-private", "Published", current.Version), [("Attachment", png)]))
            Check(noJs.IsSuccessStatusCode && Regex.IsMatch(await noJs.Content.ReadAsStringAsync(), @"!\[holiday photo\]\(/media/[a-f0-9]{32}\.webp\)(&#xA;|\s)*</textarea>"),
                "Without JavaScript, an upload is added to the end of the post text");
        var withImages = PostFields("http-private", "Published", current.Version, $"## HTTP test\n\n![holiday photo]({attachmentUrl})\n\nAfter the image.");
        using (var bannerSave = await Multipart(aliceClient, editorPath + "?handler=Save", editorPath, withImages, [("Banner", png)]))
            Check(bannerSave.StatusCode == HttpStatusCode.Found, "Saving a post with a banner succeeds");
        current = runtimePosts.FindOwned(id, alice.Id)!;
        var article = await anonymous.GetStringAsync("/posts/http-private/");
        Check(current.BannerUrl is not null && article.Contains($"src=\"{current.BannerUrl}\"") && (await anonymous.GetStringAsync("/")).Contains($"src=\"{current.BannerUrl}\""),
            "Post banners show on the article and the home page");
        Check(article.Contains($"src=\"{attachmentUrl}\"") && article.Contains("Posted by") && article.Contains("About the author"),
            "Articles show attachments inline and who posted them");
        using (var pictures = await Multipart(aliceClient, "/account/settings/?handler=Pictures", "/account/settings/", [], [("Avatar", png), ("ProfileBanner", png)]))
            Check(pictures.StatusCode == HttpStatusCode.Found, "Users can upload a profile picture and banner");
        var withAvatar = runtimeUsers.FindById(alice.Id)!;
        var profilePage = await anonymous.GetStringAsync("/users/alice/");
        Check(withAvatar.AvatarId is not null && withAvatar.BannerId is not null && profilePage.Contains(withAvatar.AvatarUrl) && profilePage.Contains(withAvatar.BannerUrl!)
            && (await anonymous.GetStringAsync("/posts/http-private/")).Contains(withAvatar.AvatarUrl), "Profiles and bylines show the user's pictures");
        var postMedia = new[] { attachmentUrl, current.BannerUrl! };
        using (var confirmation = await aliceClient.GetAsync($"/manage/delete/{id}/"))
            Check(confirmation.IsSuccessStatusCode && runtimePosts.FindOwned(id, alice.Id) is not null, "Delete confirmation GET never mutates data");
        using (var deleted = await Form(aliceClient, $"/manage/delete/{id}/", new() { ["Version"] = current.Version.ToString() }))
            Check(deleted.StatusCode == HttpStatusCode.Found && runtimePosts.FindOwned(id, alice.Id) is null, "Owner can confirm deletion");
        foreach (var url in postMedia)
            using (var gone = await anonymous.GetAsync(url)) Check(gone.StatusCode == HttpStatusCode.NotFound, $"Deleting a post deletes its image {url[7..15]}…");
        var sameDatabase = new BlogDatabase(Path.Combine(temporary, "runtime"), legacy);
        Check(Posts(sameDatabase).Published().Count == runtimePosts.Published().Count, "Reopening storage does not duplicate imported posts");
        using (var profile = await Form(aliceClient, "/account/settings/?handler=Profile", new() { ["Profile.DisplayName"] = "Alice Updated", ["Profile.Bio"] = "A test biography." }))
            Check(profile.StatusCode == HttpStatusCode.Found && (await anonymous.GetStringAsync("/users/alice/")).Contains("A test biography."), "Users can edit their profile");
        using (var wrongCurrent = await Form(aliceClient, "/account/settings/?handler=Password", new() {
            ["Password.Current"] = "incorrect-password", ["Password.New"] = "7blog-New-Password!2026", ["Password.Confirm"] = "7blog-New-Password!2026"
        })) Check(wrongCurrent.StatusCode == HttpStatusCode.OK && runtimeUsers.VerifyPassword(runtimeUsers.FindById(alice.Id)!, password), "Changing a password requires the current password");
        using (var changedPassword = await Form(aliceClient, "/account/settings/?handler=Password", new() {
            ["Password.Current"] = password, ["Password.New"] = "7blog-New-Password!2026", ["Password.Confirm"] = "7blog-New-Password!2026"
        })) Check(changedPassword.StatusCode == HttpStatusCode.Found && runtimeUsers.Authenticate("alice", "7blog-New-Password!2026") is not null, "Users can change their password");
        using (var revoked = await aliceClient.GetAsync("/manage/")) Check(revoked.StatusCode == HttpStatusCode.Found, "Changing a password ends the current browser session");
        await Login(aliceClient, "alice", "7blog-New-Password!2026");
        cli = await Cli("reset-password", "bob");
        Check(cli.ExitCode == 0, "CLI password reset succeeds against a running server");
        using (var expired = await bobClient.GetAsync("/manage/")) Check(expired.StatusCode == HttpStatusCode.Found, "Password reset revokes existing authentication cookies");
        await Login(bobClient, "bob");
        var bobFields = PostFields("bob-post", "Published"); bobFields["Input.Featured"] = "true";
        using (var bobPost = await Form(bobClient, "/manage/editor/?handler=Save", bobFields))
            Check(bobPost.StatusCode == HttpStatusCode.Found && runtimePosts.Published().Single(post => post.Slug == "bob-post").Featured == false, "Authors cannot grant themselves administrator pin permissions");

        // Admin panel: only administrators may enter.
        using (var visitorAdmin = await anonymous.GetAsync("/admin/"))
            Check(visitorAdmin.StatusCode == HttpStatusCode.Found && visitorAdmin.Headers.Location!.OriginalString.Contains("/account/login"), "Visitors are sent to sign in from the admin panel");
        foreach (var path in new[] { "/admin/", "/admin/users/", "/admin/posts/", "/admin/users/new/" })
            using (var authorAdmin = await bobClient.GetAsync(path))
                Check(authorAdmin.StatusCode == HttpStatusCode.Found && authorAdmin.Headers.Location!.OriginalString.Contains("/Status/403"), $"Authors are denied {path}");
        using (var forgedCreate = await Form(bobClient, "/admin/users/new/", new() {
            ["Username"] = "intruder", ["Rank"] = "Admin", ["Password"] = password, ["Confirm"] = password }, "/account/settings/"))
            Check(forgedCreate.StatusCode == HttpStatusCode.Found && runtimeUsers.FindByUsername("intruder") is null, "Authors cannot create accounts by posting to the admin panel");
        foreach (var path in new[] { "/admin/", "/admin/users/", "/admin/posts/" })
            using (var adminPage = await aliceClient.GetAsync(path))
                Check(adminPage.StatusCode == HttpStatusCode.OK && (await adminPage.Content.ReadAsStringAsync()).Contains("role=\"tabpanel\""), $"Administrators can open {path}");
        Check((await aliceClient.GetStringAsync("/")).Contains("href=\"/admin/\"") && !(await bobClient.GetStringAsync("/")).Contains("href=\"/admin/\""),
            "Only administrators see the admin panel link");

        using (var createdUser = await Form(aliceClient, "/admin/users/new/", new() {
            ["Username"] = "Carol", ["DisplayName"] = "Carol", ["Rank"] = "Author", ["Password"] = password, ["Confirm"] = password }))
            Check(createdUser.StatusCode == HttpStatusCode.Found && runtimeUsers.FindByUsername("carol")?.Rank == "Author", "Administrators create accounts from the panel");
        var carol = runtimeUsers.FindByUsername("carol")!;
        var carolPath = $"/admin/users/{carol.Id}/";
        using (var duplicate = await Form(aliceClient, "/admin/users/new/", new() {
            ["Username"] = "carol", ["Rank"] = "Author", ["Password"] = password, ["Confirm"] = password }))
            Check(duplicate.StatusCode == HttpStatusCode.OK && (await duplicate.Content.ReadAsStringAsync()).Contains("already exists"), "Duplicate usernames are rejected in the panel");
        using var carolClient = Client();
        await Login(carolClient, "carol");
        using (var renamed = await Form(aliceClient, carolPath + "?handler=Details", new() {
            ["Username"] = "carol-w", ["DisplayName"] = "Carol W.", ["Bio"] = "Writes about gardens.", ["Rank"] = "Author" }, carolPath))
            Check(renamed.StatusCode == HttpStatusCode.Found && runtimeUsers.FindById(carol.Id) is { Username: "carol-w", DisplayName: "Carol W." },
                "Administrators can rename users and edit their profile");
        using (var stillSignedIn = await carolClient.GetAsync("/manage/"))
            Check(stillSignedIn.StatusCode == HttpStatusCode.OK, "A rename by an administrator does not sign the user out");
        using (var taken = await Form(aliceClient, carolPath + "?handler=Details", new() {
            ["Username"] = "alice", ["DisplayName"] = "Carol W.", ["Rank"] = "Author" }, carolPath))
            Check(taken.StatusCode == HttpStatusCode.OK && runtimeUsers.FindById(carol.Id)!.Username == "carol-w", "Renaming to a taken username is refused");
        using (var selfDemote = await Form(aliceClient, $"/admin/users/{alice.Id}/?handler=Details", new() {
            ["Username"] = "alice", ["DisplayName"] = "Alice", ["Rank"] = "Author" }, $"/admin/users/{alice.Id}/"))
            Check(selfDemote.StatusCode == HttpStatusCode.OK && runtimeUsers.FindById(alice.Id)!.Rank == "Admin", "Administrators cannot demote themselves");
        using (var selfDisable = await Form(aliceClient, $"/admin/users/{alice.Id}/?handler=Status", [], $"/admin/users/{alice.Id}/"))
            Check(selfDisable.StatusCode == HttpStatusCode.OK && !runtimeUsers.FindById(alice.Id)!.Disabled, "Administrators cannot disable themselves");
        using (var promoted = await Form(aliceClient, carolPath + "?handler=Details", new() {
            ["Username"] = "carol-w", ["DisplayName"] = "Carol W.", ["Rank"] = "Admin" }, carolPath))
            Check(promoted.StatusCode == HttpStatusCode.Found && runtimeUsers.FindById(carol.Id)!.Rank == "Admin", "Administrators can promote users");
        using (var signedOut = await carolClient.GetAsync("/manage/"))
            Check(signedOut.StatusCode == HttpStatusCode.Found, "A rank change signs the user out so the new rank applies");
        await Login(carolClient, "carol-w");
        using (var carolAdmin = await carolClient.GetAsync("/admin/")) Check(carolAdmin.StatusCode == HttpStatusCode.OK, "Promoted users can open the admin panel after signing in");

        // Users change their own username in Account settings.
        using (var wrongPassword = await Form(carolClient, "/account/settings/?handler=Username", new() { ["NewUsername"] = "caroline", ["UsernamePassword"] = "incorrect-password" }))
            Check(wrongPassword.StatusCode == HttpStatusCode.OK && runtimeUsers.FindById(carol.Id)!.Username == "carol-w", "Changing your username requires your password");
        using (var selfRename = await Form(carolClient, "/account/settings/?handler=Username", new() { ["NewUsername"] = "Caroline", ["UsernamePassword"] = password }))
            Check(selfRename.StatusCode == HttpStatusCode.Found && runtimeUsers.FindById(carol.Id)!.Username == "caroline", "Users can change their own username");
        using (var afterRename = await carolClient.GetAsync("/account/settings/"))
            Check(afterRename.StatusCode == HttpStatusCode.OK, "Users stay signed in after changing their username");
        using (var oldProfile = await anonymous.GetAsync("/users/carol-w/")) Check(oldProfile.StatusCode == HttpStatusCode.NotFound, "The old profile address is released");
        using (var newProfile = await anonymous.GetAsync("/users/caroline/")) Check(newProfile.IsSuccessStatusCode, "The profile moves to the new username");
        using var carolAgain = Client();
        await Login(carolAgain, "caroline");

        // Moderation: reassign, pin and delete any post.
        var bobsPost = runtimePosts.Published().Single(post => post.Slug == "bob-post");
        var moderatePath = $"/admin/posts/{bobsPost.Id}/";
        using (var moderated = await Form(aliceClient, moderatePath + "?handler=Moderate", new() {
            ["Version"] = bobsPost.Version.ToString(), ["AuthorId"] = carol.Id, ["Status"] = "Published", ["Featured"] = "true" }, moderatePath))
            Check(moderated.StatusCode == HttpStatusCode.Found && runtimePosts.Find(bobsPost.Id) is { Featured: true } moved && moved.AuthorId == carol.Id,
                "Administrators can reassign and pin any post");
        Check((await anonymous.GetStringAsync("/posts/bob-post/")).Contains("Carol W."), "A reassigned post shows its new author");
        using (var staleModeration = await Form(aliceClient, moderatePath + "?handler=Moderate", new() {
            ["Version"] = bobsPost.Version.ToString(), ["AuthorId"] = carol.Id, ["Status"] = "Draft" }, moderatePath))
            Check(staleModeration.StatusCode == HttpStatusCode.Conflict, "Moderation detects stale edits");
        var moderatedVersion = runtimePosts.Find(bobsPost.Id)!.Version.ToString();
        using (var unconfirmed = await Form(aliceClient, moderatePath + "?handler=Delete", new() { ["Version"] = moderatedVersion }, moderatePath))
            Check(unconfirmed.StatusCode == HttpStatusCode.OK && runtimePosts.Find(bobsPost.Id) is not null, "Deleting from the panel needs confirmation");
        using (var removed = await Form(aliceClient, moderatePath + "?handler=Delete", new() { ["Version"] = moderatedVersion, ["ConfirmDelete"] = "true" }, moderatePath))
            Check(removed.StatusCode == HttpStatusCode.Found && runtimePosts.Find(bobsPost.Id) is null, "Administrators can delete any post");

        // Site settings and analytics in the admin panel.
        foreach (var path in new[] { "/admin/settings/", "/admin/analytics/" })
            using (var authorDenied = await bobClient.GetAsync(path))
                Check(authorDenied.StatusCode == HttpStatusCode.Found && authorDenied.Headers.Location!.OriginalString.Contains("/Status/403"), $"Authors are denied {path}");
        using (var savedSettings = await Multipart(aliceClient, "/admin/settings/", "/admin/settings/", new() {
            ["Name"] = "Window Notes", ["Description"] = "Tagline from the panel", ["Author"] = "Site Owner", ["About"] = "## About this place" },
            [("Icon", png), ("Header", png)]))
            Check(savedSettings.StatusCode == HttpStatusCode.Found, "Administrators can save site settings with an icon and header");
        var home = await anonymous.GetStringAsync("/");
        Check(home.Contains("<title>Window Notes</title>") && home.Contains("Tagline from the panel"), "The site name and tagline come from the admin panel");
        Check(Regex.IsMatch(home, @"<link rel=""icon"" type=""image/webp"" href=""/media/[a-f0-9]{32}\.webp""")
            && Regex.IsMatch(home, @"class=""status-bar-field footer-name""><img src=""/media/[a-f0-9]{32}\.webp"""), "The uploaded icon is the favicon and appears in the footer");
        Check(Regex.IsMatch(home, @"class=""site-header has-header-image"" style=""background-image: url\(&#x27;/media/[a-f0-9]{32}\.webp&#x27;\)"""),
            "The uploaded header background is shown behind the site name");
        Check((await anonymous.GetStringAsync("/about/")).Contains("About this place") && XDocument.Parse(await anonymous.GetStringAsync("/rss.xml")).Descendants("title").First().Value == "Window Notes",
            "The About page and RSS feed use the site settings");
        using (var invalidSettings = await Multipart(aliceClient, "/admin/settings/", "/admin/settings/", new() { ["Name"] = "", ["Author"] = "Site Owner" }, []))
            Check(invalidSettings.StatusCode == HttpStatusCode.OK && (await anonymous.GetStringAsync("/")).Contains("<title>Window Notes</title>"), "Invalid site settings are refused in the panel");

        using var reader = Client();
        reader.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 6.1) Firefox/140.0");
        await reader.GetStringAsync("/"); await reader.GetStringAsync("/archive/");
        using (var fromNews = new HttpRequestMessage(HttpMethod.Get, "/posts/hello-world/")) { fromNews.Headers.Referrer = new Uri("https://news.example/thread"); await reader.SendAsync(fromNews); }
        await aliceClient.GetStringAsync("/");
        var analyticsPage = await aliceClient.GetStringAsync("/admin/analytics/?Days=7");
        Check(Regex.IsMatch(analyticsPage, @"<legend>Page views</legend><p class=""stat-value"">3</p>") && Regex.IsMatch(analyticsPage, @"<legend>Visitors</legend><p class=""stat-value"">1</p>"),
            "Analytics count anonymous readers but not test clients or signed-in users");
        Check(analyticsPage.Contains("news.example") && analyticsPage.Contains("role=\"progressbar\""), "Analytics list referring sites with 7.css bars");
        cli = await Cli("disable", "bob");
        using (var disabled = await bobClient.GetAsync("/manage/")) Check(cli.ExitCode == 0 && disabled.StatusCode == HttpStatusCode.Found, "Disabling an account revokes its existing sessions");
        using (var logoutWithoutToken = await aliceClient.PostAsync("/account/logout/", new FormUrlEncodedContent([])))
            Check(logoutWithoutToken.StatusCode == HttpStatusCode.BadRequest, "Sign-out requires a valid CSRF token");
        using (var logout = await Form(aliceClient, "/account/logout/", [])) Check(logout.StatusCode == HttpStatusCode.Found, "User can sign out");
        using (var loggedOut = await aliceClient.GetAsync("/manage/")) Check(loggedOut.StatusCode == HttpStatusCode.Found, "Signed-out sessions cannot access author pages");
        var rss = XDocument.Parse(await anonymous.GetStringAsync("/rss.xml"));
        Check(rss.Descendants("item").Count() == runtimePosts.Published().Count, "RSS matches the public post count");
        Check(rss.Descendants("link").All(link => link.Value.StartsWith($"http://127.0.0.1:{port}/")), "RSS uses the configured public origin");
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var sitemap = XDocument.Parse(await anonymous.GetStringAsync("/sitemap.xml"));
        Check(sitemap.Descendants(ns + "loc").Any(url => url.Value.EndsWith("/users/alice/")), "Sitemap includes author profiles");
        using (var missing = await anonymous.GetAsync("/posts/does-not-exist/")) Check(missing.StatusCode == HttpStatusCode.NotFound, "Unknown posts retain HTTP 404");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}
finally
{
    SqliteConnection.ClearAllPools();
    if (!Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unexpected test cleanup path.");
    Directory.Delete(temporary, recursive: true);
}
Console.WriteLine($"\n{passed} checks passed.");
