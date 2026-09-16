using Grimarr;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
var password = builder.Configuration["GRIMARR_PASSWORD"];
if(string.IsNullOrWhiteSpace(password) || password.Length < 12) throw new InvalidOperationException("Set GRIMARR_PASSWORD to a password of at least 12 characters.");
var configRoot = builder.Configuration["GRIMARR_CONFIG"] ?? Path.Combine(AppContext.BaseDirectory,"config");
Directory.CreateDirectory(configRoot);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configRoot,"keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => {
    options.Cookie.Name = "grimarr.session"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromDays(7); options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options => options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddSingleton<Store>(); builder.Services.AddSingleton<Integrations>(); builder.Services.AddSingleton<Importer>(); builder.Services.AddSingleton<Workflow>(); builder.Services.AddHostedService<Worker>();
var app = builder.Build();
app.Use(async (context,next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' https: data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if(context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if(context.Request.Method is not ("GET" or "HEAD") && context.Request.Headers["X-Grimarr"] != "1") { context.Response.StatusCode = 403; return; }
    }
    try { await next(); }
    catch(Exception ex) when(!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = Workflow.SafeError(ex) }); }
});
app.UseDefaultFiles(); app.UseStaticFiles(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/login", async (LoginRequest input, HttpContext context) => {
    if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(input.Password ?? "")), SHA256.HashData(Encoding.UTF8.GetBytes(password)))) return Results.Unauthorized();
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name,"owner") },CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok();
}).RequireRateLimiting("login");
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/session", () => Results.Ok(new { authenticated = true }));
api.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync(); return Results.Ok(); });
api.MapGet("/settings", (Store store) => PublicSettings(store.Settings()));
api.MapPut("/settings", async (Settings input, Store store, Workflow workflow, CancellationToken ct) => {
    await workflow.Gate.WaitAsync(ct);
    try { var settings = Merge(input,store.Settings()); ValidateSettings(settings); store.Save(settings); return Results.Ok(PublicSettings(settings)); }
    finally { workflow.Gate.Release(); }
});
api.MapPost("/connections/{service}/test", async (string service, Settings input, Store store, Integrations integrations, CancellationToken ct) => {
    var settings = Merge(input,store.Settings());
    switch(service)
    {
        case "prowlarr":
            using(var client = Integrations.Client(settings.Prowlarr,"X-Api-Key")) { using var response = await client.GetAsync("api/v1/system/status",ct); response.EnsureSuccessStatusCode(); }
            break;
        case "qbittorrent": using(await integrations.Qbittorrent(settings,ct)) { } break;
        case "audiobookshelf": return Results.Ok(new { message = "Connected", libraries = await integrations.Libraries(settings,ct) });
        default: return Results.NotFound();
    }
    return Results.Ok(new { message = "Connected" });
});
api.MapGet("/books", (Store store) => store.Books().Select(PublicBook));
api.MapPost("/books", async (BookInput input, Store store, Workflow workflow, CancellationToken ct) => {
    if(string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Author) || input.Title.Length > 250 || input.Author.Length > 200) return Results.BadRequest(new { error = "Enter a title and author (maximum 250 and 200 characters)." });
    if(input.Cover.Length > 2000 || input.CatalogId.Length > 200) return Results.BadRequest();
    await workflow.Gate.WaitAsync(ct);
    try {
        if(store.Books().Any(b => b.Title.Equals(input.Title.Trim(),StringComparison.OrdinalIgnoreCase) && b.Author.Equals(input.Author.Trim(),StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new { error = "This book is already in your library." });
        var preferences = input.Preferences ?? store.Settings().Defaults; ValidatePreferences(preferences);
        var book = new Book { Title = input.Title.Trim(), Author = input.Author.Trim(), Cover = Uri.TryCreate(input.Cover,UriKind.Absolute,out var cover) && cover.Scheme == "https" ? input.Cover : "", CatalogId = input.CatalogId, Preferences = preferences };
        store.Save(book); store.Log(book,"Added and monitored for automatic download"); return Results.Ok(PublicBook(book));
    }
    finally { workflow.Gate.Release(); }
});
api.MapPost("/books/{id}/retry", async (string id, Store store, Workflow workflow, CancellationToken ct) => {
    await workflow.Gate.WaitAsync(ct);
    try { var book = store.Books().FirstOrDefault(b => b.Id == id); if(book == null) return Results.NotFound(); book.NextSearch = DateTimeOffset.MinValue; book.Suspended = false; store.Save(book); return Results.Ok(); }
    finally { workflow.Gate.Release(); }
});
api.MapPost("/books/{id}/pause", async (string id, Store store, Workflow workflow, CancellationToken ct) => {
    await workflow.Gate.WaitAsync(ct);
    try { var book = store.Books().FirstOrDefault(b => b.Id == id); if(book == null) return Results.NotFound(); book.Suspended = true; store.Save(book); return Results.Ok(); }
    finally { workflow.Gate.Release(); }
});
api.MapPut("/books/{id}/preferences", async (string id, Preferences preferences, Store store, Workflow workflow, CancellationToken ct) => {
    ValidatePreferences(preferences); await workflow.Gate.WaitAsync(ct);
    try { var book = store.Books().FirstOrDefault(b => b.Id == id); if(book == null) return Results.NotFound(); if(book.Status != "wanted") return Results.Conflict(new { error = "An edition has already been selected for this download." }); book = book with { Preferences = preferences, NextSearch = DateTimeOffset.MinValue }; store.Save(book); return Results.Ok(); }
    finally { workflow.Gate.Release(); }
});
api.MapGet("/books/{id}/releases", async (string id, Store store, Workflow workflow, CancellationToken ct) => {
    var book = store.Books().FirstOrDefault(b => b.Id == id); if(book == null) return Results.NotFound();
    var releases = await workflow.Search(book,store.Settings(),ct);
    return Results.Ok(releases.Select(r => new { release = PublicRelease(r.Release), r.Score, r.Eligible, r.Reasons }));
});
api.MapGet("/activity", (Store store) => store.Activities());
api.MapGet("/catalog", async (string q, CancellationToken ct) => {
    if(q.Length is < 2 or > 200) return Results.BadRequest(new { error = "Search must contain 2–200 characters." });
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    using var response = await client.GetAsync("https://itunes.apple.com/search?media=audiobook&entity=audiobook&country=US&limit=24&term=" + Uri.EscapeDataString(q),ct); response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    return Results.Ok(json.RootElement.GetProperty("results").EnumerateArray().Select(r => new { title = Integrations.Text(r,"collectionName"), author = Integrations.Text(r,"artistName"), cover = Integrations.Text(r,"artworkUrl100"), catalogId = "itunes:" + (r.TryGetProperty("collectionId",out var id) ? id.ToString() : ""), source = "Apple Books", rating = (double?)null }).ToArray());
});
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

static object PublicRelease(Release release) => new { release.Title, release.Indexer, release.Seeders, release.Size, release.ListenerRating, release.RatingCount, release.RatingSource };
static object PublicBook(Book book) => new { book.Id, book.Title, book.Author, book.Cover, book.CatalogId, book.Preferences, book.Status, book.Suspended, book.Message, book.Progress, book.TorrentHash, selectedRelease = book.SelectedRelease == null ? null : PublicRelease(book.SelectedRelease), book.ImportedPath, book.AddedAt, book.NextSearch };
static object PublicSettings(Settings settings) => new { settings = settings with { Prowlarr = settings.Prowlarr with { ApiKey = "", Password = "" }, Qbittorrent = settings.Qbittorrent with { ApiKey = "", Password = "" }, Audiobookshelf = settings.Audiobookshelf with { ApiKey = "", Password = "" } }, secrets = new { prowlarr = settings.Prowlarr.ApiKey.Length > 0, qbittorrent = settings.Qbittorrent.Password.Length > 0, audiobookshelf = settings.Audiobookshelf.ApiKey.Length > 0 } };
static Settings Merge(Settings input, Settings old) => input with {
    Prowlarr = input.Prowlarr with { ApiKey = string.IsNullOrEmpty(input.Prowlarr.ApiKey) ? old.Prowlarr.ApiKey : input.Prowlarr.ApiKey },
    Qbittorrent = input.Qbittorrent with { Password = string.IsNullOrEmpty(input.Qbittorrent.Password) ? old.Qbittorrent.Password : input.Qbittorrent.Password },
    Audiobookshelf = input.Audiobookshelf with { ApiKey = string.IsNullOrEmpty(input.Audiobookshelf.ApiKey) ? old.Audiobookshelf.ApiKey : input.Audiobookshelf.ApiKey }
};
static void ValidatePreferences(Preferences preferences)
{
    if(preferences.Narration is not ("either" or "standard" or "dramatized") || preferences.Abridgement is not ("either" or "abridged" or "unabridged") || preferences.MinimumSeeders is < 0 or > 100000) throw new InvalidOperationException("Invalid edition preferences.");
}
static void ValidateSettings(Settings settings)
{
    Integrations.BaseUrl(settings.Prowlarr.Url); Integrations.BaseUrl(settings.Qbittorrent.Url); Integrations.BaseUrl(settings.Audiobookshelf.Url);
    ValidatePreferences(settings.Defaults);
    if(settings.SearchIntervalMinutes is < 15 or > 10080) throw new InvalidOperationException("Search interval must be between 15 minutes and seven days.");
    if(settings.ImportMode is not ("copy" or "hardlink-or-copy")) throw new InvalidOperationException("Unknown import mode.");
    if(!Path.IsPathFullyQualified(settings.LibraryRoot) || !Path.IsPathFullyQualified(settings.DownloadRoot)) throw new InvalidOperationException("Storage roots must be absolute paths inside the Grimarr container.");
    var library = Path.GetFullPath(settings.LibraryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var downloads = Path.GetFullPath(settings.DownloadRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if(library.StartsWith(downloads,StringComparison.OrdinalIgnoreCase) || downloads.StartsWith(library,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Download and library roots must be separate, non-nested directories.");
    foreach(var mapping in settings.PathMappings) if(string.IsNullOrWhiteSpace(mapping.Remote) || !Path.IsPathFullyQualified(mapping.Local)) throw new InvalidOperationException("Path mappings need a remote prefix and an absolute local path.");
    if(settings.IndexerIds.Any(id => id <= 0)) throw new InvalidOperationException("Indexer IDs must be positive numbers.");
    if(settings.AutomationEnabled && (string.IsNullOrWhiteSpace(settings.Prowlarr.ApiKey) || string.IsNullOrWhiteSpace(settings.LibraryId) || string.IsNullOrWhiteSpace(settings.Audiobookshelf.ApiKey))) throw new InvalidOperationException("Configure Prowlarr and Audiobookshelf credentials and select a library before enabling automation.");
}
record LoginRequest(string Password);
record BookInput(string Title, string Author, string Cover = "", string CatalogId = "", Preferences? Preferences = null);
public partial class Program { }
