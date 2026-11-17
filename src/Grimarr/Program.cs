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
builder.Services.AddRateLimiter(options => {
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("catalog", _ => RateLimitPartition.GetFixedWindowLimiter("apple-books", _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.RejectionStatusCode=429;
});
builder.Services.AddSingleton<Store>(); builder.Services.AddSingleton<Integrations>(); builder.Services.AddSingleton<Importer>(); builder.Services.AddSingleton<AppleCatalog>(); builder.Services.AddSingleton<Workflow>(); builder.Services.AddHostedService<Worker>();
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
    try {
        var old = store.Settings(); var settings = Merge(input,old); ValidateSettings(settings); store.Save(settings);
        if (old.ManualSelection != settings.ManualSelection || (string.IsNullOrWhiteSpace(old.Prowlarr.ApiKey) && !string.IsNullOrWhiteSpace(settings.Prowlarr.ApiKey)))
            foreach (var book in store.Books().Where(b=>b.Status=="wanted")) { book.NextSearch=DateTimeOffset.MinValue; store.Save(book); }
        if (!old.AutomationEnabled && settings.AutomationEnabled)
            foreach (var book in store.Books().Where(b=>b.Status=="downloading")) { book.LastDownloadCheck=null; book.MissingSince=null; store.Save(book); }
        return Results.Ok(PublicSettings(settings));
    }
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
api.MapPost("/books/quick-add/preview", async (HttpContext context, CancellationToken ct) => {
    const long maximumBody=24*1024*1024;
    if (context.Request.ContentLength>maximumBody) return Results.Json(new { error="Torrent upload is too large (16 MB maximum file size)." },statusCode:413);
    var limit=context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly:false }) limit.MaxRequestBodySize=maximumBody;
    var input=await context.Request.ReadFromJsonAsync<QuickAddInput>(ct);
    if (input==null) return Results.BadRequest(new { error="Provide a magnet link, info hash, or torrent file." });
    return Results.Ok(QuickAdd.Preview(input));
});
api.MapPost("/books/quick-add", async (HttpContext context, Store store, Workflow workflow, AppleCatalog catalog, CancellationToken ct) => {
    const long maximumBody=24*1024*1024;
    if (context.Request.ContentLength>maximumBody) return Results.Json(new { error="Torrent upload is too large (16 MB maximum file size)." },statusCode:413);
    var limit=context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly:false }) limit.MaxRequestBodySize=maximumBody;
    var input=await context.Request.ReadFromJsonAsync<QuickAddInput>(ct);
    if (input==null) return Results.BadRequest(new { error="Enter the book details and a download source." });
    await workflow.Gate.WaitAsync(ct);
    try {
        var book=catalog.Apply(QuickAdd.Create(input,store.Settings().Defaults),input.CatalogSelectionId); var books=store.Books();
        if (books.Any(b=>b.Title.Equals(book.Title,StringComparison.OrdinalIgnoreCase) && b.Author.Equals(book.Author,StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new { error="This book is already in your library. Open it to retry or choose a replacement." });
        var hash=book.SelectedRelease!.InfoHash!;
        if (books.Any(b=>hash.Equals(b.TorrentHash,StringComparison.OrdinalIgnoreCase) || hash.Equals(b.SelectedRelease?.InfoHash,StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new { error="This torrent is already tracked by Grimarr." });
        store.Save(book);store.Log(book,"Added through Quick Add; exact release queued for download");return Results.Ok(PublicBook(book));
    }
    finally { workflow.Gate.Release(); }
});
api.MapPost("/books", async (BookInput input, Store store, Workflow workflow, AppleCatalog catalog, CancellationToken ct) => {
    if(string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Author) || input.Title.Length > 250 || input.Author.Length > 200) return Results.BadRequest(new { error = "Enter a title and author (maximum 250 and 200 characters)." });
    if(input.Cover.Length > 2000 || input.CatalogId.Length > 200) return Results.BadRequest();
    await workflow.Gate.WaitAsync(ct);
    try {
        if(store.Books().Any(b => b.Title.Equals(input.Title.Trim(),StringComparison.OrdinalIgnoreCase) && b.Author.Equals(input.Author.Trim(),StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new { error = "This book is already in your library." });
        var preferences = input.Preferences ?? store.Settings().Defaults; ValidatePreferences(preferences);
        var book = catalog.Apply(new Book { Title = input.Title.Trim(), Author = input.Author.Trim(), Cover = Uri.TryCreate(input.Cover,UriKind.Absolute,out var cover) && cover.Scheme == "https" ? input.Cover : "", CatalogId = input.CatalogId, Preferences = preferences },input.CatalogSelectionId);
        store.Save(book); store.Log(book,"Added and monitored for automatic download"); return Results.Ok(PublicBook(book));
    }
    finally { workflow.Gate.Release(); }
});
api.MapDelete("/books/{id}", async (string id, Store store, Workflow workflow, CancellationToken ct) => {
    await workflow.Gate.WaitAsync(ct);
    try {
        var book = store.Books().FirstOrDefault(b => b.Id == id);
        if (book == null) return Results.NotFound();
        if (!book.CanRemove) return Results.Conflict(new { error = "This book has started downloading or importing. Pause monitoring instead." });
        store.Remove(book);
        return Results.NoContent();
    }
    finally { workflow.Gate.Release(); }
});
api.MapPost("/books/{id}/retry", async (string id, Store store, Workflow workflow, CancellationToken ct) => {
    await workflow.Gate.WaitAsync(ct);
    try { var book = store.Books().FirstOrDefault(b => b.Id == id); if(book == null) return Results.NotFound(); workflow.Retry(book); return Results.Ok(); }
    finally { workflow.Gate.Release(); }
});
api.MapPost("/books/{id}/library-review",async (string id,LibraryReviewInput input,Store store,Workflow workflow,CancellationToken ct)=>{
    await workflow.Gate.WaitAsync(ct);
    try { var book=store.Books().FirstOrDefault(b=>b.Id==id);if(book==null)return Results.NotFound();await workflow.ReviewLibrary(store.Settings(),book,input.Action,input.ItemId,ct);return Results.Ok(PublicBook(book)); }
    finally { workflow.Gate.Release(); }
});
api.MapPost("/books/{id}/replace", async (string id, Store store, Workflow workflow, CancellationToken ct) => {
    await workflow.Gate.WaitAsync(ct);
    try { var book=store.Books().FirstOrDefault(b=>b.Id==id); if(book==null) return Results.NotFound(); workflow.Replace(book); return Results.Ok(); }
    finally { workflow.Gate.Release(); }
});
api.MapPost("/books/{id}/clear-blocklist", async (string id, Store store, Workflow workflow, CancellationToken ct) => {
    await workflow.Gate.WaitAsync(ct);
    try {
        var book=store.Books().FirstOrDefault(b=>b.Id==id); if(book==null) return Results.NotFound();
        for (var i=0;i<book.FailedReleases.Count;i++) book.FailedReleases[i]=book.FailedReleases[i] with { Blocked=false };
        book.NextSearch=DateTimeOffset.MinValue; store.Save(book); store.Log(book,"Failed-release blocklist cleared; history retained"); return Results.Ok();
    }
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
api.MapGet("/books/{id}/releases", async (string id, string? q, Store store, Workflow workflow, CancellationToken ct) => {
    if (q != null && (string.IsNullOrWhiteSpace(q) || q.Length > 500)) return Results.BadRequest(new { error="Enter a search query between 1 and 500 characters." });
    await workflow.Gate.WaitAsync(ct);
    try {
        var book = store.Books().FirstOrDefault(b => b.Id == id); if(book == null) return Results.NotFound();
        var settings = store.Settings();
        if (settings.ManualSelection && book.Status is "wanted" or "available")
        {
            var choices = await workflow.SearchChoices(book,settings,q,ct);
            return Results.Ok(choices.Select(c => new { c.SelectionId, c.AlreadyTracked, canSelect=!c.AlreadyTracked && Workflow.CanChoose(c.Ranked.Release) && !book.FailedReleases.Any(f=>f.Blocked && Workflow.SameRelease(f,c.Ranked.Release)), release=PublicRelease(c.Ranked.Release), c.Ranked.Score, c.Ranked.Eligible, c.Ranked.Reasons }));
        }
        var releases = await workflow.Search(book,settings,ct);
        return Results.Ok(releases.Select(r => new { release = PublicRelease(r.Release), r.Score, r.Eligible, r.Reasons }));
    }
    finally { workflow.Gate.Release(); }
});
api.MapPost("/books/{id}/select", async (string id, SelectionInput input, Store store, Workflow workflow, CancellationToken ct) => {
    if (input.SelectionIds == null) return Results.BadRequest(new { error="Select at least one release." });
    await workflow.Gate.WaitAsync(ct);
    try {
        if (!store.Settings().ManualSelection) return Results.Conflict(new { error="Turn on manual release selection in Settings first." });
        var book = store.Books().FirstOrDefault(b=>b.Id==id); if(book==null) return Results.NotFound();
        return Results.Ok((await workflow.Select(book,input.SelectionIds,ct)).Select(PublicBook));
    }
    finally { workflow.Gate.Release(); }
});
api.MapGet("/activity", (Store store) => store.Activities());
api.MapGet("/catalog", async (string q, AppleCatalog catalog, CancellationToken ct) => Results.Ok(await catalog.Search(q,ct))).RequireRateLimiting("catalog");
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

static object PublicRelease(Release release) => new { release.Title, release.Indexer, release.Seeders, release.SeedersKnown, release.Size, release.ListenerRating, release.RatingCount, release.RatingSource };
static object PublicBook(Book book) => new { book.Id, book.Title, book.Author, book.Cover, book.CatalogId, book.Description, book.PublishedDate, book.Genre, book.CatalogUrl, book.Preferences, book.Status, book.AcquisitionMode, book.Suspended, book.Message, book.Progress, book.TorrentHash, book.CanRemove, book.LibraryMatches,book.AudiobookshelfItemId,book.VerifiedLibraryId,selectedRelease = book.SelectedRelease == null ? null : PublicRelease(book.SelectedRelease), book.ImportedPath, book.AddedAt, book.NextSearch, book.FailureReason, failedReleases=book.FailedReleases.Select(f=>new { f.Title, f.Reason, f.At, f.Blocked, f.InfoHash }) };
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
    if (!string.IsNullOrWhiteSpace(settings.Prowlarr.Url) || !string.IsNullOrWhiteSpace(settings.Prowlarr.ApiKey)) Integrations.BaseUrl(settings.Prowlarr.Url);
    Integrations.BaseUrl(settings.Qbittorrent.Url); Integrations.BaseUrl(settings.Audiobookshelf.Url);
    ValidatePreferences(settings.Defaults);
    if(settings.SearchIntervalMinutes is < 15 or > 10080) throw new InvalidOperationException("Search interval must be between 15 minutes and seven days.");
    if(settings.StallTimeoutMinutes != 0 && settings.StallTimeoutMinutes is < 15 or > 10080) throw new InvalidOperationException("Stall timeout must be zero (disabled), or between 15 minutes and seven days.");
    if(settings.ImportMode is not ("copy" or "hardlink-or-copy")) throw new InvalidOperationException("Unknown import mode.");
    if(!Path.IsPathFullyQualified(settings.LibraryRoot) || !Path.IsPathFullyQualified(settings.DownloadRoot)) throw new InvalidOperationException("Storage roots must be absolute paths inside the Grimarr container.");
    if(!LibraryAvailability.ValidRemoteRoot(settings.AudiobookshelfLibraryPath))throw new InvalidOperationException("Audiobookshelf library path must be blank or an absolute path as seen by Audiobookshelf, without traversal segments.");
    var library = Path.GetFullPath(settings.LibraryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var downloads = Path.GetFullPath(settings.DownloadRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if(library.StartsWith(downloads,StringComparison.OrdinalIgnoreCase) || downloads.StartsWith(library,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Download and library roots must be separate, non-nested directories.");
    foreach(var mapping in settings.PathMappings) if(string.IsNullOrWhiteSpace(mapping.Remote) || !Path.IsPathFullyQualified(mapping.Local)) throw new InvalidOperationException("Path mappings need a remote prefix and an absolute local path.");
    if(settings.IndexerIds.Any(id => id <= 0)) throw new InvalidOperationException("Indexer IDs must be positive numbers.");
    if(settings.AutomationEnabled && (string.IsNullOrWhiteSpace(settings.LibraryId) || string.IsNullOrWhiteSpace(settings.Audiobookshelf.ApiKey))) throw new InvalidOperationException("Configure Audiobookshelf credentials and select a library before enabling automation. Prowlarr is optional for Quick Add.");
}
record LoginRequest(string Password);
record BookInput(string Title, string Author, string Cover = "", string CatalogId = "", Preferences? Preferences = null, string? CatalogSelectionId = null);
record SelectionInput(string[] SelectionIds);
record LibraryReviewInput(string Action,string? ItemId=null);
public partial class Program { }
