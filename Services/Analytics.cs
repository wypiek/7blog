using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SevenBlog.Services;

public sealed record AnalyticsDay(DateOnly Day, int Views, int Visitors);
public sealed record AnalyticsCount(string Key, int Views);
public sealed record AnalyticsReport(IReadOnlyList<AnalyticsDay> Days, IReadOnlyList<AnalyticsCount> Pages, IReadOnlyList<AnalyticsCount> Referrers)
{
    public int Views => Days.Sum(day => day.Views);
    public int Visitors => Days.Sum(day => day.Visitors);
}

// Privacy-friendly, server-side analytics. No cookies, no client script, no IP addresses stored.
// Unique visitors are counted per day with a salted hash of IP + user agent; the salt rotates every
// UTC day and the hashes are deleted once the day is over, so visitors cannot be followed across days.
public sealed partial class AnalyticsCollector(BlogDatabase database, IOptions<BlogOptions> options)
{
    private static readonly string[] PrivatePaths = ["/admin", "/manage", "/account", "/status", "/error", "/media"];
    private readonly Lock gate = new();
    private Dictionary<(string Day, string Path), int> pages = [];
    private Dictionary<(string Day, string Host), int> referrers = [];
    private HashSet<(string Day, string Hash)> visitors = [];
    private string saltDay = "";
    private byte[] salt = [];

    public static bool ShouldCount(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) || context.Response.StatusCode != StatusCodes.Status200OK) return false;
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true) return false;
        if (context.User.Identity?.IsAuthenticated == true) return false;
        if (PrivatePaths.Any(path => request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))) return false;
        var agent = request.Headers.UserAgent.ToString();
        return agent.Length > 0 && !Bot().IsMatch(agent);
    }

    public void Record(HttpContext context)
    {
        var day = Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = (context.Request.Path.Value ?? "/").ToLowerInvariant().TrimEnd('/') + "/";
        if (path.Length > 200) path = path[..200];
        var referrer = ReferrerHost(context);
        // Respect Do Not Track / Global Privacy Control: count the page view, not the visitor.
        var track = context.Request.Headers["DNT"] != "1" && context.Request.Headers["Sec-GPC"] != "1";
        lock (gate)
        {
            pages[(day, path)] = pages.GetValueOrDefault((day, path)) + 1;
            if (referrer is not null) referrers[(day, referrer)] = referrers.GetValueOrDefault((day, referrer)) + 1;
            if (track) visitors.Add((day, VisitorHash(day, context)));
        }
    }

    public void Flush()
    {
        Dictionary<(string Day, string Path), int> pageBatch; Dictionary<(string Day, string Host), int> referrerBatch; HashSet<(string Day, string Hash)> visitorBatch;
        lock (gate)
        {
            if (pages.Count == 0 && visitors.Count == 0) return;
            (pageBatch, referrerBatch, visitorBatch) = (pages, referrers, visitors);
            (pages, referrers, visitors) = ([], [], []);
        }
        using var connection = database.Open(); using var transaction = connection.BeginTransaction();
        void Run(string sql, params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }
        foreach (var ((day, path), views) in pageBatch)
            Run("INSERT INTO analytics_pages(day,path,views) VALUES($day,$key,$views) ON CONFLICT(day,path) DO UPDATE SET views=views+excluded.views",
                ("$day", day), ("$key", path), ("$views", views));
        foreach (var ((day, host), views) in referrerBatch)
            Run("INSERT INTO analytics_referrers(day,host,views) VALUES($day,$key,$views) ON CONFLICT(day,host) DO UPDATE SET views=views+excluded.views",
                ("$day", day), ("$key", host), ("$views", views));
        foreach (var (day, hash) in visitorBatch)
            Run("INSERT OR IGNORE INTO analytics_visitors(day,hash) VALUES($day,$hash)", ("$day", day), ("$hash", hash));
        foreach (var day in visitorBatch.Select(visitor => visitor.Day).Distinct())
            Run("""
                INSERT INTO analytics_days(day,visitors) SELECT $day, COUNT(*) FROM analytics_visitors WHERE day=$day
                ON CONFLICT(day) DO UPDATE SET visitors=excluded.visitors
                """, ("$day", day));
        // Yesterday's hashes are kept briefly for late batches, then only the daily count remains.
        Run("DELETE FROM analytics_visitors WHERE day<$keep", ("$keep", Today().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        transaction.Commit();
    }

    public AnalyticsReport Report(int days)
    {
        Flush();
        var today = Today();
        var from = today.AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var connection = database.Open();
        List<(string, int)> Rows(string sql)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            command.Parameters.AddWithValue("$from", from);
            using var reader = command.ExecuteReader(); var rows = new List<(string, int)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt32(1)));
            return rows;
        }
        var views = Rows("SELECT day, SUM(views) FROM analytics_pages WHERE day>=$from GROUP BY day").ToDictionary();
        var unique = Rows("SELECT day, visitors FROM analytics_days WHERE day>=$from").ToDictionary();
        var series = Enumerable.Range(0, days).Select(offset => today.AddDays(-offset)).Select(day => {
            var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new AnalyticsDay(day, views.GetValueOrDefault(key), unique.GetValueOrDefault(key));
        }).ToArray();
        var top = Rows("SELECT path, SUM(views) AS total FROM analytics_pages WHERE day>=$from GROUP BY path ORDER BY total DESC, path LIMIT 10");
        var sources = Rows("SELECT host, SUM(views) AS total FROM analytics_referrers WHERE day>=$from GROUP BY host ORDER BY total DESC, host LIMIT 10");
        return new AnalyticsReport(series, top.Select(row => new AnalyticsCount(row.Item1, row.Item2)).ToArray(),
            sources.Select(row => new AnalyticsCount(row.Item1, row.Item2)).ToArray());
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private string? ReferrerHost(HttpContext context)
    {
        if (!Uri.TryCreate(context.Request.Headers.Referer.ToString(), UriKind.Absolute, out var referrer)
            || referrer.Scheme is not ("http" or "https")) return null;
        var host = referrer.IdnHost.ToLowerInvariant();
        var own = new Uri(options.Value.SiteUrl).IdnHost.ToLowerInvariant();
        if (host == own || host == context.Request.Host.Host.ToLowerInvariant()) return null;
        return host.Length > 100 ? host[..100] : host;
    }

    // Called inside the lock. The salt lives in the settings table only for the current day.
    private string VisitorHash(string day, HttpContext context)
    {
        if (saltDay != day)
        {
            using var connection = database.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key='analytics.salt'";
            var stored = (command.ExecuteScalar() as string)?.Split('|');
            if (stored is [var storedDay, var value] && storedDay == day) salt = Convert.FromBase64String(value);
            else
            {
                salt = RandomNumberGenerator.GetBytes(32);
                command.CommandText = "INSERT INTO settings(key,value) VALUES('analytics.salt',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                command.Parameters.AddWithValue("$value", $"{day}|{Convert.ToBase64String(salt)}");
                command.ExecuteNonQuery();
            }
            saltDay = day;
        }
        var input = Encoding.UTF8.GetBytes($"{context.Connection.RemoteIpAddress}|{context.Request.Headers.UserAgent}");
        return Convert.ToHexString(HMACSHA256.HashData(salt, input), 0, 16);
    }

    [GeneratedRegex(@"bot|crawl|spider|slurp|preview|monitor|curl|wget|python|httpclient|headless|java/|go-http|okhttp|feed|fetch|scan",
        RegexOptions.IgnoreCase)]
    private static partial Regex Bot();
}

public sealed class AnalyticsFlusher(AnalyticsCollector collector, ILogger<AnalyticsFlusher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) Flush(); }
        catch (OperationCanceledException) { }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Flush();
    }
    private void Flush()
    {
        try { collector.Flush(); }
        catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
        { logger.LogWarning(error, "Saving analytics failed; this batch of page views was dropped."); }
    }
}
