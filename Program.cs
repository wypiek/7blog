using System.IO.Compression;
using System.Net;
using System.Xml.Linq;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using SevenBlog.Services;

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { using var response = await client.GetAsync("http://127.0.0.1:8080/healthz"); Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1; }
    catch (HttpRequestException) { Environment.ExitCode = 1; }
    catch (TaskCanceledException) { Environment.ExitCode = 1; }
    return;
}

var accountCommand = args.FirstOrDefault() == "users";
var builder = WebApplication.CreateBuilder(accountCommand ? [] : args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddRazorPages(options => {
    options.Conventions.AuthorizeFolder("/Manage");
    options.Conventions.AuthorizePage("/Account/Settings");
    options.Conventions.AuthorizePage("/Account/Logout");
    options.Conventions.AuthorizeFolder("/Admin", AccountSession.AdminPolicy);
});
builder.Services.AddOptions<BlogOptions>().Bind(builder.Configuration.GetSection("Blog"))
    .Validate(options => Uri.TryCreate(options.SiteUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment),
        "Blog:SiteUrl must be an absolute http(s) origin, e.g. https://blog.example.com.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Name) && !string.IsNullOrWhiteSpace(options.Author),
        "Blog:Name and Blog:Author are required.")
    .ValidateOnStart();
builder.Services.AddSingleton<BlogDatabase>();
builder.Services.AddSingleton<IPasswordHasher<BlogUser>, PasswordHasher<BlogUser>>();
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
builder.Services.AddSingleton<UserStore>();
builder.Services.AddScoped<PostStore>();
builder.Services.AddScoped<BlogStore>();
builder.Services.AddSingleton<MediaStore>();
builder.Services.AddSingleton<SiteSettingsStore>();
builder.Services.AddSingleton<AnalyticsCollector>();
builder.Services.AddHostedService<AnalyticsFlusher>();
builder.Services.AddHostedService<MediaCleanup>();
var configuredBlog = builder.Configuration.GetSection("Blog").Get<BlogOptions>() ?? new BlogOptions();
var dataPath = Path.GetFullPath(configuredBlog.DataPath, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataPath);
builder.Services.AddDataProtection().SetApplicationName("7blog").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
var secureCookies = configuredBlog.SiteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
builder.Services.AddAntiforgery(options => {
    options.Cookie.Name = "7blog.csrf";
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
// Caddy terminates TLS. Apply the configured public origin's cookie policy
// without requiring antiforgery to see HTTPS on the internal HTTP connection.
builder.Services.Configure<CookiePolicyOptions>(options =>
    options.Secure = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => {
    options.Cookie.Name = "7blog.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/Status/403";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnValidatePrincipal = async context => {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
        var id = UserStore.UserId(context.Principal!);
        var user = id is null ? null : users.FindById(id);
        if (user is null || user.Disabled || user.SecurityStamp != context.Principal!.FindFirstValue(UserStore.StampClaim))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    };
});
// The role claim comes from the signed cookie; rank changes rotate the security stamp,
// so a demoted administrator is signed out on their next request.
builder.Services.AddAuthorization(options =>
    options.AddPolicy(AccountSession.AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("Admin")));
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});
builder.Services.AddResponseCompression(options => {
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/rss+xml", "image/svg+xml"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<ForwardedHeadersOptions>(options => {
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    // Trust only explicit proxy IPs/networks in addition to framework loopback defaults.
    // Under Docker, host Caddy reaches Kestrel through the bridge gateway, not loopback.
    foreach (var value in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        if (!string.IsNullOrWhiteSpace(value)) options.KnownProxies.Add(IPAddress.Parse(value));
    foreach (var value in builder.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
        if (!string.IsNullOrWhiteSpace(value)) options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(value));
});
// The container health check calls http://127.0.0.1:8080, so loopback names stay
// allowed even when AllowedHosts is restricted to the public domain.
builder.Services.PostConfigure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(options => {
    if (options.AllowedHosts.Count == 0 || options.AllowedHosts.Contains("*")) return;
    options.AllowedHosts = options.AllowedHosts.Concat(["localhost", "127.0.0.1"])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
});

var app = builder.Build();
// Load and validate all content at startup; a broken release fails before accepting requests.
_ = app.Services.GetRequiredService<BlogDatabase>();
if (!accountCommand && app.Services.GetRequiredService<UserStore>().ClaimUnownedPosts() is > 0 and var claimed)
    app.Logger.LogInformation("Assigned {Count} unowned posts to the first administrator.", claimed);
if (accountCommand)
{
    Environment.ExitCode = UserCommands.Run(args.Skip(1).ToArray(), app.Services.GetRequiredService<UserStore>());
    return;
}
app.UseForwardedHeaders();
app.UseCookiePolicy();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.Use(async (context, next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    await next();
});
app.UseResponseCompression();
app.UseStatusCodePagesWithReExecute("/Status/{0}");
app.UseStaticFiles(new StaticFileOptions {
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "public,max-age=86400"
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// Count public page views after the page has rendered (see AnalyticsCollector for what is excluded).
app.Use(async (context, next) => {
    await next();
    if (AnalyticsCollector.ShouldCount(context)) context.RequestServices.GetRequiredService<AnalyticsCollector>().Record(context);
});
app.Use(async (context, next) => {
    if (context.User.Identity?.IsAuthenticated == true || context.Request.Path.StartsWithSegments("/account")
        || context.Request.Path.StartsWithSegments("/manage") || context.Request.Path.StartsWithSegments("/admin"))
    {
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
    await next();
});
app.MapGet("/healthz", () => Results.Text("healthy", "text/plain"));
app.MapGet("/rss.xml", (BlogStore store, IOptions<BlogOptions> options, SiteSettingsStore site) => {
    var blog = options.Value;
    var settings = site.Current;
    XNamespace atom = "http://www.w3.org/2005/Atom";
    var channel = new XElement("channel", new XElement("title", settings.Name),
        new XElement("description", settings.Description), new XElement("link", blog.Absolute("/")),
        new XElement("language", "en"),
        new XElement(atom + "link", new XAttribute("href", blog.Absolute("/rss.xml")),
            new XAttribute("rel", "self"), new XAttribute("type", "application/rss+xml")),
        store.Posts.Select(post => new XElement("item", new XElement("title", post.Title),
            new XElement("description", post.Description), new XElement("link", blog.Absolute(post.Url)),
            new XElement("guid", new XAttribute("isPermaLink", "true"), blog.Absolute(post.Url)),
            new XElement("pubDate", post.Date.ToString("R")),
            post.Tags.Select(tag => new XElement("category", tag)))));
    var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
        new XElement("rss", new XAttribute("version", "2.0"), new XAttribute(XNamespace.Xmlns + "atom", atom), channel));
    return Results.Text("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + document, "application/rss+xml; charset=utf-8");
});
app.MapGet("/sitemap.xml", (BlogStore store, IOptions<BlogOptions> options) => {
    XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
    var paths = new[] { "/", "/about/", "/archive/" }.Concat(store.Posts.Select(post => post.Url))
        .Concat(store.Tags.Select(tag => $"/tags/{tag}/"))
        .Concat(store.Posts.Where(post => post.AuthorUrl is not null).Select(post => post.AuthorUrl!).Distinct());
    return Results.Text(new XElement(ns + "urlset", paths.Select(path =>
        new XElement(ns + "url", new XElement(ns + "loc", options.Value.Absolute(path))))).ToString(), "application/xml; charset=utf-8");
});
// Uploaded images. Each file name is a random id that never changes, so it can be cached forever.
app.MapGet("/media/{file}", (string file, MediaStore media, HttpContext context) => {
    if (!MediaStore.IsFileName(file)) return Results.NotFound();
    var path = media.FilePath(file[..^5]);
    if (!File.Exists(path)) return Results.NotFound();
    context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
    return Results.File(path, "image/webp");
});
app.MapGet("/robots.txt", (IOptions<BlogOptions> options) =>
    Results.Text($"User-agent: *\nAllow: /\nSitemap: {options.Value.Absolute("/sitemap.xml")}\n", "text/plain"));
app.MapRazorPages();
app.Run();

public partial class Program { }
