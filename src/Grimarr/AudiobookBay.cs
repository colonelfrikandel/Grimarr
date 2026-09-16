using AngleSharp.Html.Parser;
using System.Net;
using System.Text.RegularExpressions;

namespace Grimarr;

// Jackett supplies search results; ABB's structured detail fields supply edition evidence.
// Never infer a language from the site's overall language or its navigation/sidebar.
public sealed class AudiobookBay : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset Expires, Details? Value)> cache = new();
    private DateTimeOffset nextRequest;
    public record Details(string Title, string? Language, string? Abridgement, string? Narration, string Hash);

    public static bool IsDetailUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "audiobookbay.lu" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath.StartsWith("/abss/", StringComparison.Ordinal) && uri.AbsolutePath.Length > 6;

    public static Details ParseDetails(string html)
    {
        using var doc = new HtmlParser().ParseDocument(html);
        var title = doc.QuerySelector(".postTitle h1")?.TextContent.Trim() ?? "";
        var languages = doc.QuerySelectorAll(".postInfo [itemprop='inLanguage']").Select(e => e.TextContent.Trim().ToLowerInvariant()).Distinct().ToArray();
        var language = languages.Length == 1 ? languages[0] : languages.Length > 1 ? "conflicting" : null;
        var lengths = doc.QuerySelectorAll(".postContent .desc .is_abridged").Select(e => e.TextContent.Trim().ToLowerInvariant()).Distinct().ToArray();
        var abridgement = lengths.Length == 1 && lengths[0] is "abridged" or "unabridged" ? lengths[0] : null;
        var categories = string.Join(" ", doc.QuerySelectorAll(".postInfo a[rel='category tag']").Select(e => e.TextContent));
        var narration = Regex.IsMatch(title + " " + categories, @"\b(full[ -]cast|dramatized|dramatised|audio immersion tunnel|graphic[ -]?audio)\b", RegexOptions.IgnoreCase) ? "dramatized" : null;
        var hashes = doc.QuerySelectorAll(".postContent tr").Select(row => row.QuerySelectorAll("td")).Where(cells => cells.Length == 2 && cells[0].TextContent.Trim() == "Info Hash:").Select(cells => cells[1].TextContent.Trim().ToLowerInvariant()).Distinct().ToArray();
        if (title.Length == 0 || hashes.Length != 1 || !Regex.IsMatch(hashes[0], "^[a-f0-9]{40}$")) throw new InvalidOperationException("Audiobook Bay details have no confirmed title or torrent hash");
        return new(title, language, abridgement, narration, hashes[0]);
    }

    public static Release Apply(Release release, Details details)
    {
        if (!Ranking.ContainsWords(release.Title, details.Title)) return release with { MetadataError = "Audiobook Bay detail title differs from the search result" };
        return release with {
            Language = details.Language, Abridgement = details.Abridgement, Narration = details.Narration,
            InfoHash = details.Hash, MagnetUrl = "magnet:?xt=urn:btih:" + details.Hash + "&dn=" + Uri.EscapeDataString(details.Title),
            Seeders = 0, SeedersKnown = false, MetadataError = null
        };
    }

    public async Task Enrich(Book book, List<Release> releases, CancellationToken ct, bool manual = false)
    {
        int fetched = 0;
        for (int i = 0; i < releases.Count; i++)
        {
            var release = releases[i];
            if (!IsDetailUrl(release.Guid)) continue;
            releases[i] = release = release with { SeedersKnown = false, Seeders = 0, MetadataError = "Audiobook Bay details have not been confirmed" };
            // Only fetch plausible matches, without relaxing the final ranking requirements.
            var probe = release with { MetadataError = null, Language = "en", Abridgement = book.Preferences.Abridgement, Narration = book.Preferences.Narration == "dramatized" ? "dramatized" : null, SeedersKnown = true, Seeders = book.Preferences.MinimumSeeders };
            if (!manual && !Ranking.Rank(book, probe).Eligible) continue;
            await gate.WaitAsync(ct);
            try
            {
                Details? details;
                if (cache.TryGetValue(release.Guid, out var cached) && cached.Expires > DateTimeOffset.UtcNow) details = cached.Value;
                else
                {
                    if (fetched >= (manual ? 10 : 3)) continue;
                    fetched++;
                    var delay = nextRequest - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                    details = null;
                    try
                    {
                        using var response = await client.GetAsync(release.Guid, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        const int maxBytes = 2 * 1024 * 1024;
                        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidOperationException("Details page too large");
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(12));
                        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                        using var buffer = new MemoryStream();
                        var chunk = new byte[8192]; int count;
                        while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                        {
                            if (buffer.Length + count > maxBytes) throw new InvalidOperationException("Details page too large");
                            buffer.Write(chunk, 0, count);
                        }
                        details = ParseDetails(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
                    finally { nextRequest = DateTimeOffset.UtcNow.AddSeconds(6.2); }
                    if (cache.Count >= 128) cache.Clear();
                    cache[release.Guid] = (DateTimeOffset.UtcNow.AddMinutes(details == null ? 5 : 360), details);
                }
                releases[i] = details == null ? release with { MetadataError = "Audiobook Bay details unavailable; try again later" } : Apply(release, details);
            }
            finally { gate.Release(); }
        }
    }
    public void Dispose() { client.Dispose(); gate.Dispose(); }
}
