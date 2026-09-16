using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Grimarr;

public sealed class Integrations
{
    public static Uri BaseUrl(string url)
    {
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Enter an HTTP or HTTPS URL without credentials, query, or fragment.");
        return uri;
    }
    public static HttpClient Client(Connection connection, string? header = null)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = BaseUrl(connection.Url), Timeout = TimeSpan.FromSeconds(45) };
        if (header == "bearer") client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        else if (header != null) client.DefaultRequestHeaders.Add(header, connection.ApiKey);
        return client;
    }
    public async Task<List<Release>> Search(Settings settings, Book book, CancellationToken ct)
    {
        using var client = Client(settings.Prowlarr, "X-Api-Key");
        var query = $"api/v1/search?query={Uri.EscapeDataString(book.Title + " " + book.Author)}&type=search&categories=3030";
        foreach (var id in settings.IndexerIds) query += $"&indexerIds={id}";
        using var response = await client.GetAsync(query, ct); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.EnumerateArray().Select(r => new Release {
            Guid = Text(r,"guid"), Title = Text(r,"title"), Indexer = Text(r,"indexer"), IndexerId = Number(r,"indexerId"),
            Protocol = r.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == JsonValueKind.Number ? (protocol.GetInt32() == 2 ? "torrent" : "usenet") : Text(r,"protocol"),
            DownloadUrl = Text(r,"downloadUrl"), MagnetUrl = Text(r,"magnetUrl"), InfoHash = Text(r,"infoHash"), Seeders = Number(r,"seeders"),
            Size = r.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) ? bytes : 0
        }).ToList();
    }
    public async Task<JsonElement> Libraries(Settings settings, CancellationToken ct)
    {
        using var client = Client(settings.Audiobookshelf, "bearer");
        using var response = await client.GetAsync("api/libraries", ct); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("libraries").Clone();
    }
    public async Task Scan(Settings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.LibraryId)) throw new InvalidOperationException("Select an Audiobookshelf library in Settings.");
        using var client = Client(settings.Audiobookshelf, "bearer");
        using var response = await client.PostAsync($"api/libraries/{Uri.EscapeDataString(settings.LibraryId)}/scan", null, ct);
        response.EnsureSuccessStatusCode();
    }
    public async Task<Qbit> Qbittorrent(Settings settings, CancellationToken ct)
    {
        var client = Client(settings.Qbittorrent);
        client.DefaultRequestHeaders.Referrer = client.BaseAddress;
        try
        {
            using var response = await client.PostAsync("api/v2/auth/login", new FormUrlEncodedContent(new Dictionary<string,string> { ["username"] = settings.Qbittorrent.Username, ["password"] = settings.Qbittorrent.Password }), ct);
            response.EnsureSuccessStatusCode();
            if ((await response.Content.ReadAsStringAsync(ct)).Trim() != "Ok.") throw new InvalidOperationException("qBittorrent login failed. Check username and password.");
            return new Qbit(client);
        }
        catch { client.Dispose(); throw; }
    }
    public async Task<(string Hash, byte[]? Torrent, string? Magnet)> Prepare(Settings settings, Release release, CancellationToken ct)
    {
        var magnet = !string.IsNullOrEmpty(release.MagnetUrl) ? release.MagnetUrl : release.DownloadUrl?.StartsWith("magnet:") == true ? release.DownloadUrl : null;
        if (magnet != null) return (TorrentIdentity.MagnetHash(magnet), null, magnet);
        var baseUri = BaseUrl(settings.Prowlarr.Url);
        var url = new Uri(baseUri, release.DownloadUrl ?? "");
        // Never forward the Prowlarr API key to an indexer or a redirect target.
        if (url.GetLeftPart(UriPartial.Authority) != baseUri.GetLeftPart(UriPartial.Authority) || !url.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidOperationException("Release has no Prowlarr proxy download URL or magnet.");
        using var client = Client(settings.Prowlarr, "X-Api-Key");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location?.Scheme == "magnet")
        { var location = response.Headers.Location.ToString(); return (TorrentIdentity.MagnetHash(location), null, location); }
        response.EnsureSuccessStatusCode();
        const int maximum = 16 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidOperationException("Torrent metadata exceeds 16 MB.");
        using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + count > maximum) throw new InvalidOperationException("Torrent metadata exceeds 16 MB."); await buffer.WriteAsync(chunk.AsMemory(0,count), ct); }
        var bytes = buffer.ToArray(); return (TorrentIdentity.Hash(bytes), bytes, null);
    }
    public static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public static int Number(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}

public sealed class Qbit(HttpClient client) : IDisposable
{
    public void Dispose() => client.Dispose();
    public async Task<JsonElement[]> Info(string hash, CancellationToken ct)
    {
        using var response = await client.GetAsync("api/v2/torrents/info?hashes=" + Uri.EscapeDataString(hash), ct); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
    }
    public async Task Add(Settings settings, Book book, byte[]? torrent, string? magnet, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        if (torrent != null) content.Add(new ByteArrayContent(torrent), "torrents", "audiobook.torrent");
        else content.Add(new StringContent(magnet!), "urls");
        content.Add(new StringContent("grimarr"), "category");
        content.Add(new StringContent("grimarr-" + book.Id), "tags");
        content.Add(new StringContent("false"), "autoTMM");
        if (!string.IsNullOrWhiteSpace(settings.QbitSavePath)) content.Add(new StringContent(settings.QbitSavePath), "savepath");
        using var response = await client.PostAsync("api/v2/torrents/add", content, ct); response.EnsureSuccessStatusCode();
        if ((await response.Content.ReadAsStringAsync(ct)).Trim() != "Ok.") throw new InvalidOperationException("qBittorrent did not accept the torrent.");
    }
}
