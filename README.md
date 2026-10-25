# 7blog

A small, self-hosted blog engine built with **ASP.NET Core 10 Razor Pages** and [7.css](https://khang-nd.github.io/7.css/).

Markdown posts, author accounts with a browser editor, profile pictures and banners, post banners, inline post images, full-text search, topics, an archive, author profiles, RSS, and a sitemap. Data lives in a single SQLite file plus a folder of compressed images.

## Images

- **Profile picture and banner**: set in *Account settings*. The picture is cropped square to 256 px and the banner to 4:1 (1600×400). The picture appears on the profile, in each post's byline, in the post list, and in the post's *About the author* box.
- **Post banner**: set in the editor. It is cropped to 8:3 (1600×600) and shown above the post and on the home page.
- **Post images**: upload them in the editor's *Images in this post* box, or use the toolbar's **Image…** button, paste, or drop a file into the text. The image is inserted at the cursor as an ordinary Markdown line (`![alt](/media/….webp)`), so it can go in any section. Without JavaScript, an upload is added to the end of the text and you move the line where you want it.

Every upload is decoded, resized (photos to at most 1600 px), and re-encoded as WebP at quality 80, so only small files are stored and served. Originals are never kept, which also strips EXIF metadata such as GPS location. Phone photos are rotated upright first. Uploads are limited to 12 MB per file and 60 per user per hour. At most two images are processed at once, so uploads cannot saturate the server. Images are served from `/media/` with a one-year immutable cache. Images uploaded for a post that is never saved are removed after a day, and deleting a post deletes its images.

## Design rules

The interface uses **only 7.css components**, kept deliberately restrained:

| Where | 7.css component |
| --- | --- |
| Main navigation | menu bar (`ul[role=menubar]`) |
| Header and search page | search box (`.searchbox`) |
| Sidebar panels | windows (`.window` / `.title-bar` / `.window-body`) |
| Topics, archive | tree view (`ul.tree-view`) |
| My posts | list view (`table`) with 7.css scroll bars |
| Forms, notices, errors | group boxes (`fieldset`), buttons, text boxes, select, checkbox |
| Editor | tabs (`menu[role=tablist]`) and status bar |
| Footer | status bar |

`wwwroot/css/site.css` contains page layout, article typography and image framing. It defines no new colours, gradients or badges. There is no wallpaper, taskbar, or decorative title-bar buttons. Motion follows Windows 7: sidebar windows open with a short scale-and-fade, menu highlights and notices fade, and 7.css buttons glow on hover. All motion is turned off for visitors who prefer reduced motion. The pinned 7.css 0.21.1 stylesheet is served locally from `wwwroot/vendor/`, with no CDN.

Public pages work without JavaScript. The editor loads one small script (`wwwroot/js/editor.js`) for the formatting toolbar, live preview, image insertion at the cursor and Ctrl+S. Without it, the editor still works through server-side preview and uploads.

## Deploy with Docker Compose behind your existing Caddy

Requires Docker Engine with the Compose plugin on Linux (x64 or arm64). .NET is built inside Docker. **Compose builds and runs only the blog.** It never pulls, creates or starts Caddy. The only images it pulls are Microsoft's .NET SDK/runtime base images, needed to build the blog.

```sh
cp .env.example .env
# Edit .env: BLOG_SITE_URL, BLOG_AUTHOR, BLOG_ALLOWED_HOSTS
docker compose up -d --build
docker compose ps                        # wait for "healthy"
curl http://127.0.0.1:8080/healthz
```

Create the first administrator. You will be asked for the password twice. Posts that have no owner yet (such as the imported starter posts) are credited to the oldest administrator automatically, so their bylines show that account instead of `BLOG_AUTHOR`:

```sh
docker compose exec blog dotnet SevenBlog.dll users create admin --rank Admin --display-name "Your name"
```

Then sign in at `/account/login/`. After that, manage everything from the **Admin panel** (`/admin/`, in the account menu; administrators only):

- **Users**: create accounts, change usernames, display names, bios and ranks, reset passwords, disable or enable accounts, and remove profile pictures. You cannot demote or disable your own account, and the last active administrator cannot be removed. Rank changes, password resets and disabling sign the user out everywhere.
- **Posts**: every post from every author, including drafts. Change a post's author, publish or unpublish it, pin it, or delete it with its images.
- **Analytics**: page views, visitors, busiest day, top pages, referring sites and a day-by-day table for the last 7, 30 or 90 days (UTC). Counting happens on the server with no cookies or tracking scripts, and no IP addresses are stored. Unique visitors are recognised with a salted hash that changes every day and is deleted when the day ends. Signed-in users, bots, admin and editor pages, errors and feeds are not counted. Browsers sending Do Not Track or Global Privacy Control count as page views but not as visitors. Counts are written to the database in batches every 30 seconds.
- **Settings**: site name, tagline, default author name and About page text (Markdown), plus a **site icon** (browser tab and footer) and a **header background** image (cropped to 4:1, shown behind the site name). These override the `BLOG_NAME`, `BLOG_DESCRIPTION` and `BLOG_AUTHOR` values from `.env`, which remain the defaults until you save.
- **Overview**: account and post counts, plus image and database storage use.

Anyone can change their own username in *Account settings* (it needs their current password). The profile moves to `/users/<new-name>/`, and the old address stops working.

The command line is still available for recovery (for example if no administrator can sign in): `users list`, `users create NAME`, `users reset-password NAME`, `users set-rank NAME Admin|Author`, `users disable|enable NAME`.

### Caddy on the host

The container is published on `127.0.0.1:8080` only, so it is not reachable from outside except through Caddy. Merge this into your existing Caddyfile (also in `Caddyfile.example`):

```caddyfile
blog.example.com {
    encode zstd gzip
    reverse_proxy 127.0.0.1:8080
}
```

```sh
caddy validate --config /etc/caddy/Caddyfile
sudo systemctl reload caddy   # or: caddy reload --config /etc/caddy/Caddyfile
```

Caddy terminates TLS and forwards plain HTTP to the blog. If you change `BLOG_PORT`, change the upstream to match.

### Caddy already running in Docker

Set `CADDY_NETWORK` in `.env` to the **existing** network your Caddy container uses, then:

```sh
docker compose -f compose.yaml -f compose.caddy-network.yaml up -d --build
```

In that Caddy's config, use `reverse_proxy sevenblog:8080`. The override joins the existing external network with the `sevenblog` alias. It does not create the network or add a Caddy service.

### Proxy trust and allowed hosts

- `BLOG_ALLOWED_HOSTS` should be your public domain. `localhost` and `127.0.0.1` are always added so the container health check keeps working.
- Forwarded headers are trusted from loopback, `BLOG_KNOWN_PROXY`, and `BLOG_TRUSTED_NETWORK`. The default is `172.16.0.0/12`, which covers Docker's default bridge ranges. With this, the login rate limiter sees each visitor's real IP instead of the Docker gateway. If your Docker uses a different address pool, set `BLOG_TRUSTED_NETWORK` to it.
- Links, RSS and the sitemap always use `BLOG_SITE_URL`, whatever headers a request carries.

### Operations

- Data: posts, accounts, uploaded images (`media/`) and data-protection keys are in the `blog-data` volume (`/app/data`). Older databases are upgraded automatically on start. Back it up with `docker compose stop blog && docker compose cp blog:/app/data ./backup-data && docker compose start blog`.
- Content import: Markdown files in `content/posts/` are imported **once**, on first start with an empty database. After that, write and edit posts in the browser.
- Updates: `docker compose build --pull && docker compose up -d`. This updates only the blog and its .NET base images.
- Hardening: non-root user, read-only root filesystem, tmpfs `/tmp`, all capabilities dropped, `no-new-privileges`, bounded memory (512 MB, enough for decoding large photos)/CPU/PIDs/logs. The health check runs the app itself (`--healthcheck`), so the runtime image needs no curl. Image processing uses SkiaSharp (MIT) with its self-contained Linux native library, so the image needs no extra system packages (x64 and arm64).
- Logs: `docker compose logs --tail=100 blog`.

## Run locally

With the .NET 10 SDK installed:

```sh
dotnet run --project SevenBlog.csproj --urls http://localhost:8080
dotnet run --project SevenBlog.csproj -- users create admin --rank Admin --display-name "Your name"
```

On Windows, `scripts/Start-TestServer.ps1` installs a private .NET 10 SDK into `.tools/` if needed, runs the checks, and starts the server. Local data goes to `.data/`. Settings are in `appsettings.json` and can be overridden with environment variables such as `Blog__SiteUrl`.

## Starter content format

`content/posts/my-first-post.json` + `content/posts/my-first-post.md`:

```json
{
  "title": "My first post",
  "description": "A short introduction for the post list, search, and the feed.",
  "date": "2026-10-25T12:00:00Z",
  "tags": ["life", "thoughts"],
  "featured": false,
  "draft": false
}
```

Slugs and tags use lowercase letters, digits and hyphens. Raw HTML in Markdown is disabled and unsafe link schemes are removed.

## Checks

```sh
dotnet build SevenBlog.csproj --configuration Release
dotnet run --project tests/SevenBlog.Tests/SevenBlog.Tests.csproj --configuration Release
docker compose config --quiet   # needs .env
```

The test runner covers accounts, ownership, CSRF, drafts, lockout, Markdown safety, every main route, security headers and the XML feeds against a live server process.
