namespace Grimarr;

public sealed class Workflow(Store store, Integrations integrations, Importer importer)
{
    public SemaphoreSlim Gate { get; } = new(1,1);
    private readonly Dictionary<string, (string BookId, Release Release, DateTimeOffset Expires)> choices = new();
    public static bool CanChoose(Release release) => !string.IsNullOrWhiteSpace(release.Title) && release.Protocol.Equals("torrent",StringComparison.OrdinalIgnoreCase)
        && release.Size > 0 && (!string.IsNullOrWhiteSpace(release.MagnetUrl) || !string.IsNullOrWhiteSpace(release.DownloadUrl) || AudiobookBay.IsDetailUrl(release.Guid));
    // Called under Gate: choices stay server-side so clients cannot submit arbitrary download URLs.
    public async Task<List<ReleaseChoice>> SearchChoices(Book book, Settings settings, string? query, CancellationToken ct)
    {
        var releases = await integrations.Search(settings,book,ct,query,manual:true);
        foreach (var id in choices.Where(c=>c.Value.Expires <= DateTimeOffset.UtcNow).Select(c=>c.Key).ToArray()) choices.Remove(id);
        if (choices.Count + releases.Count > 1000) choices.Clear();
        var result = new List<ReleaseChoice>();
        var tracked = store.Books().Where(b=>b.SelectedRelease!=null).ToList();
        foreach (var release in releases.Take(100))
        {
            var id = Guid.NewGuid().ToString("N");
            choices[id] = (book.Id,release,DateTimeOffset.UtcNow.AddMinutes(30));
            var alreadyTracked = tracked.Any(b=>(b.SelectedRelease!.Guid==release.Guid && b.SelectedRelease.IndexerId==release.IndexerId)
                || (!string.IsNullOrEmpty(release.InfoHash) && (b.SelectedRelease.InfoHash==release.InfoHash || b.TorrentHash==release.InfoHash)));
            result.Add(new(id,Ranking.Rank(book,release),alreadyTracked));
        }
        return result.OrderByDescending(c=>c.Ranked.Eligible).ThenByDescending(c=>c.Ranked.Score).ToList();
    }
    public async Task<List<Book>> Select(Book book, string[] ids, CancellationToken ct)
    {
        var keepOriginal = book.Status == "available";
        if (!keepOriginal && (book.Status != "wanted" || !book.CanRemove)) throw new InvalidOperationException("This book is already downloading. Wait for it to finish before choosing more releases.");
        if (ids.Length is < 1 or > 10 || ids.Distinct().Count() != ids.Length) throw new InvalidOperationException("Select between one and ten different releases.");
        var releases = new List<Release>();
        foreach (var id in ids)
        {
            if (!choices.TryGetValue(id,out var choice) || choice.BookId != book.Id || choice.Expires <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("These search results expired. Search again before selecting.");
            if (!CanChoose(choice.Release)) throw new InvalidOperationException("One selected result has no supported torrent download.");
            releases.Add(choice.Release);
        }
        if (releases.Select(r=>(r.IndexerId,r.Guid)).Distinct().Count() != releases.Count) throw new InvalidOperationException("The same release was selected more than once.");
        await integrations.VerifyChoices(book,releases,ct);
        foreach (var release in releases)
            if (release.MetadataError is { Length: > 0 }) throw new InvalidOperationException(release.MetadataError);
        for (int i=0;i<releases.Count;i++)
            if (!string.IsNullOrEmpty(releases[i].MagnetUrl)) releases[i]=releases[i] with { InfoHash=TorrentIdentity.MagnetHash(releases[i].MagnetUrl!) };
        var hashes = releases.Where(r=>!string.IsNullOrEmpty(r.InfoHash)).Select(r=>r.InfoHash).ToList();
        if (hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=hashes.Count) throw new InvalidOperationException("The selected results include the same torrent more than once.");
        var existing = store.Books().Where(b=>keepOriginal || b.Id!=book.Id).ToList();
        foreach (var release in releases)
            if (existing.Any(b=>b.SelectedRelease is { } selected && ((selected.Guid==release.Guid && selected.IndexerId==release.IndexerId) || (!string.IsNullOrEmpty(release.InfoHash) && (selected.InfoHash==release.InfoHash || b.TorrentHash==release.InfoHash)))))
                throw new InvalidOperationException("One selected release is already in your library.");
        var selectedBooks = releases.Select((release,i)=>new Book {
            Id=i==0 && !keepOriginal ? book.Id : Guid.NewGuid().ToString("N"), Title=release.Title[..Math.Min(release.Title.Length,250)], Author=book.Author,
            Preferences=book.Preferences, Status="queued", Message="Manually selected; queued for download", SelectedRelease=release
        }).ToList();
        store.SaveSelection(selectedBooks);
        foreach(var id in choices.Where(c=>c.Value.BookId==book.Id).Select(c=>c.Key).ToArray()) choices.Remove(id);
        return selectedBooks;
    }
    public async Task<List<RankedRelease>> Search(Book book, Settings settings, CancellationToken ct) =>
        (await integrations.Search(settings,book,ct)).Select(r => Ranking.Rank(book,r)).OrderByDescending(r => r.Eligible).ThenByDescending(r => r.Score).ThenBy(r => r.Release.Guid,StringComparer.Ordinal).ToList();
    public async Task Tick(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var settings = store.Settings(); if(!settings.AutomationEnabled) return;
            foreach(var book in store.Books().Where(b => b.Status != "available" && !b.Suspended))
            {
                if(book.NextSearch > DateTimeOffset.UtcNow) continue;
                try { await Advance(settings,book,ct); }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
                catch(Exception ex)
                {
                    book.Message = SafeError(ex); book.NextSearch = DateTimeOffset.UtcNow.AddMinutes(5);
                    store.Save(book); store.Log(book,book.Message);
                }
            }
        }
        finally { Gate.Release(); }
    }
    private async Task Advance(Settings settings, Book book, CancellationToken ct)
    {
        if(book.Status == "wanted")
        {
            if (settings.ManualSelection)
            {
                book.Message = "Waiting for you to search and choose releases.";
                book.NextSearch = DateTimeOffset.UtcNow.AddMinutes(settings.SearchIntervalMinutes);
                store.Save(book); return;
            }
            var releases = await Search(book,settings,ct);
            var best = releases.FirstOrDefault(r => r.Eligible);
            if(best == null)
            {
                book.Message = releases.Count == 0 ? "No releases found. Monitoring for new releases." : "No confirmed match. Review release reasons or adjust edition preferences.";
                book.NextSearch = DateTimeOffset.UtcNow.AddMinutes(settings.SearchIntervalMinutes); store.Save(book); store.Log(book,book.Message); return;
            }
            book.SelectedRelease = best.Release; book.Status = "queued"; book.Message = "Preparing selected release"; store.Save(book);
        }
        if(book.Status == "queued")
        {
            var prepared = await integrations.Prepare(settings,book.SelectedRelease!,ct);
            if(book.TorrentHash != null && book.TorrentHash != prepared.Hash) throw new InvalidOperationException("Release torrent identity changed; download stopped for review.");
            book.TorrentHash = prepared.Hash; store.Save(book); // Save identity before the remote mutation, for restart-safe recovery.
            using var qbit = await integrations.Qbittorrent(settings,ct);
            if((await qbit.Info(prepared.Hash,ct)).Length == 0) await qbit.Add(settings,book,prepared.Torrent,prepared.Magnet,ct);
            book.Status = "downloading"; book.Message = "Sent to qBittorrent"; store.Save(book); store.Log(book,book.Message);
        }
        if(book.Status == "downloading")
        {
            using var qbit = await integrations.Qbittorrent(settings,ct);
            var torrents = await qbit.Info(book.TorrentHash!,ct);
            if(torrents.Length == 0) throw new InvalidOperationException("Torrent not found in qBittorrent. Restore it to resume tracking.");
            var torrent = torrents[0]; var state = Integrations.Text(torrent,"state");
            if(state is "error" or "missingFiles") throw new InvalidOperationException("qBittorrent reports an error or missing files. Check the download client.");
            book.Progress = torrent.GetProperty("progress").GetDouble();
            book.Message = "qBittorrent: " + state;
            var complete = book.Progress >= 1 && torrent.TryGetProperty("amount_left",out var left) && left.GetInt64() == 0 && !state.StartsWith("checking") && state is not ("moving" or "metaDL" or "allocating");
            if(!complete) { store.Save(book); return; }
            book.Message = "Validating and importing audio"; store.Save(book);
            book.ImportedPath = await importer.Import(settings,book,Integrations.Text(torrent,"content_path"),ct);
            book.Status = "scan-pending"; book.Message = "Imported; waiting for Audiobookshelf scan"; store.Save(book); store.Log(book,book.Message);
        }
        if(book.Status == "scan-pending")
        {
            await integrations.Scan(settings,ct);
            book.Status = "available"; book.Message = "Imported. Audiobookshelf scan requested."; book.Progress = 1; store.Save(book); store.Log(book,book.Message);
        }
    }
    public static string SafeError(Exception ex) => ex switch
    {
        HttpRequestException http => $"Integration request failed{(http.StatusCode.HasValue ? $" (HTTP {(int)http.StatusCode.Value})" : "")}. Check connection settings and service availability.",
        TaskCanceledException => "Integration timed out. Retrying in five minutes.",
        InvalidOperationException => ex.Message,
        UnauthorizedAccessException => "Storage access denied. Check container user and folder permissions.",
        System.ComponentModel.Win32Exception => "ffprobe is unavailable. Use the Docker image or install FFmpeg.",
        IOException => "Storage operation failed. Check mounts, free space, and permissions.",
        _ => "Operation failed. Check service compatibility and settings; retry scheduled."
    };
}
public sealed class Worker(Workflow workflow, ILogger<Worker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try { await workflow.Tick(stoppingToken); }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { return; }
            catch(Exception) { logger.LogError("Workflow cycle failed; retrying on the next cycle."); }
            var interval = int.TryParse(configuration["GRIMARR_POLL_SECONDS"],out var seconds) ? Math.Clamp(seconds,1,300) : 15;
            await Task.Delay(TimeSpan.FromSeconds(interval),stoppingToken);
        }
    }
}
