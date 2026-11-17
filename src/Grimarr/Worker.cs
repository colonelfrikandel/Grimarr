namespace Grimarr;

public sealed class Workflow(Store store, Integrations integrations, Importer importer, IConfiguration? configuration = null)
{
    public SemaphoreSlim Gate { get; } = new(1,1);
    private readonly Dictionary<string, (string BookId, Release Release, DateTimeOffset Expires)> choices = new();
    public static bool SameRelease(FailedRelease failed, Release release, string? hash = null) =>
        (!string.IsNullOrEmpty(failed.InfoHash) && (failed.InfoHash.Equals(hash,StringComparison.OrdinalIgnoreCase) || failed.InfoHash.Equals(release.InfoHash,StringComparison.OrdinalIgnoreCase)))
        || (!string.IsNullOrWhiteSpace(failed.Guid) && failed.Guid == release.Guid && failed.IndexerId == release.IndexerId);
    private static RankedRelease Rank(Book book, Release release)
    {
        var ranked = Ranking.Rank(book,release);
        return book.FailedReleases.Any(f=>f.Blocked && SameRelease(f,release))
            ? ranked with { Eligible=false, Reasons=[..ranked.Reasons,"Blocked after a failed download"] } : ranked;
    }
    private static Release WithHash(Release release)
    {
        var magnet=string.IsNullOrEmpty(release.MagnetUrl) ? release.DownloadUrl : release.MagnetUrl;
        if (magnet?.StartsWith("magnet:",StringComparison.OrdinalIgnoreCase)==true)
        {
            try { return release with { InfoHash=TorrentIdentity.MagnetHash(magnet) }; }
            catch(InvalidOperationException) { /* Prepare reports invalid torrent metadata before submission. */ }
        }
        return release;
    }
    public void Retry(Book book)
    {
        if (book.Status == "download-failed")
        {
            for (var i=0;i<book.FailedReleases.Count;i++)
                if (book.SelectedRelease is {} release && SameRelease(book.FailedReleases[i],release,book.TorrentHash))
                    book.FailedReleases[i] = book.FailedReleases[i] with { Blocked=false };
            book.Status="queued"; book.FailureReason=null; book.MissingSince=null;
            book.Message="Retrying the same release; existing torrent will be tracked if present";
        }
        if (book.Status == "import-failed") { book.Status="import-pending"; book.Message="Retrying import"; }
        book.NextSearch=DateTimeOffset.MinValue; book.Suspended=false;
        book.LastProgressAt=DateTimeOffset.UtcNow; book.LastDownloadCheck=null;
        store.Save(book); store.Log(book,"Retry requested");
    }
    public void Replace(Book book)
    {
        if (book.Status != "download-failed") throw new InvalidOperationException("Only a failed download can be replaced. Resolve import or connection errors and retry instead.");
        // History and the new search state are one persisted book update.
        book.Status="wanted"; book.SelectedRelease=null; book.TorrentHash=null; book.Progress=0;
        book.AcquisitionMode="prowlarr";
        book.FailureReason=null; book.MissingSince=null; book.LastProgressAt=null; book.LastDownloadCheck=null;
        book.DownloadPath=null; book.NextSearch=DateTimeOffset.MinValue; book.Suspended=false;
        book.Message="Searching for a replacement; failed releases remain blocked. Existing torrents and files are untouched.";
        store.Save(book); store.Log(book,book.Message);
    }
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
        foreach (var release in releases.Take(100).Select(WithHash))
        {
            var id = Guid.NewGuid().ToString("N");
            choices[id] = (book.Id,release,DateTimeOffset.UtcNow.AddMinutes(30));
            var alreadyTracked = tracked.Any(b=>(b.SelectedRelease!.Guid==release.Guid && b.SelectedRelease.IndexerId==release.IndexerId)
                || (!string.IsNullOrEmpty(release.InfoHash) && (b.SelectedRelease.InfoHash==release.InfoHash || b.TorrentHash==release.InfoHash)));
            result.Add(new(id,Rank(book,release),alreadyTracked));
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
            if (book.FailedReleases.Any(f=>f.Blocked && SameRelease(f,choice.Release))) throw new InvalidOperationException("This release is blocked after a failed download. Clear the blocklist to select it again.");
            releases.Add(choice.Release);
        }
        if (releases.Select(r=>(r.IndexerId,r.Guid)).Distinct().Count() != releases.Count) throw new InvalidOperationException("The same release was selected more than once.");
        await integrations.VerifyChoices(book,releases,ct);
        foreach (var release in releases)
            if (release.MetadataError is { Length: > 0 }) throw new InvalidOperationException(release.MetadataError);
        for (int i=0;i<releases.Count;i++)
            if (!string.IsNullOrEmpty(releases[i].MagnetUrl)) releases[i]=releases[i] with { InfoHash=TorrentIdentity.MagnetHash(releases[i].MagnetUrl!) };
        if (releases.Any(r=>book.FailedReleases.Any(f=>f.Blocked && SameRelease(f,r)))) throw new InvalidOperationException("This torrent is blocked after a failed download.");
        var hashes = releases.Where(r=>!string.IsNullOrEmpty(r.InfoHash)).Select(r=>r.InfoHash).ToList();
        if (hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=hashes.Count) throw new InvalidOperationException("The selected results include the same torrent more than once.");
        var existing = store.Books().Where(b=>keepOriginal || b.Id!=book.Id).ToList();
        foreach (var release in releases)
            if (existing.Any(b=>b.SelectedRelease is { } selected && ((selected.Guid==release.Guid && selected.IndexerId==release.IndexerId) || (!string.IsNullOrEmpty(release.InfoHash) && (selected.InfoHash==release.InfoHash || b.TorrentHash==release.InfoHash)))))
                throw new InvalidOperationException("One selected release is already in your library.");
        var selectedBooks = releases.Select((release,i)=>new Book {
            Id=i==0 && !keepOriginal ? book.Id : Guid.NewGuid().ToString("N"), Title=release.Title[..Math.Min(release.Title.Length,250)], Author=book.Author,
            Preferences=book.Preferences, Status="queued", Message="Manually selected; queued for download", SelectedRelease=release,
            FailedReleases=[..book.FailedReleases]
        }).ToList();
        store.SaveSelection(selectedBooks);
        foreach(var id in choices.Where(c=>c.Value.BookId==book.Id).Select(c=>c.Key).ToArray()) choices.Remove(id);
        return selectedBooks;
    }
    public async Task<List<RankedRelease>> Search(Book book, Settings settings, CancellationToken ct) =>
        (await integrations.Search(settings,book,ct)).Select(WithHash).Select(r => Rank(book,r)).OrderByDescending(r => r.Eligible).ThenByDescending(r => r.Score).ThenBy(r => r.Release.Guid,StringComparer.Ordinal).ToList();
    public async Task Tick(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var settings = store.Settings(); if(!settings.AutomationEnabled) return;
            foreach(var book in store.Books().Where(b => b.Status is not ("available" or "import-failed") && !b.Suspended))
            {
                if (book.Status == "download-failed")
                {
                    if (settings.AutomaticFailedDownloadRecovery && !settings.ManualSelection && book.AcquisitionMode!="quick-add") Replace(book);
                    else continue;
                }
                if(book.NextSearch > DateTimeOffset.UtcNow) continue;
                try { await Advance(settings,book,ct); }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
                catch(Exception ex)
                {
                    // Connection failures must not count as observed stall/missing time.
                    book.LastDownloadCheck=null; book.MissingSince=null;
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
            if (string.IsNullOrWhiteSpace(settings.Prowlarr.ApiKey))
            {
                book.Message="Configure Prowlarr in Settings to search releases, or add a download with Quick Add.";
                book.NextSearch=DateTimeOffset.UtcNow.AddMinutes(settings.SearchIntervalMinutes);store.Save(book);return;
            }
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
            using var qbit = await integrations.Qbittorrent(settings,ct);
            if (book.TorrentHash == null || (await qbit.Info(book.TorrentHash,ct)).Length == 0)
            {
                var prepared = await integrations.Prepare(settings,book.SelectedRelease!,ct);
                if(book.TorrentHash != null && book.TorrentHash != prepared.Hash) throw new InvalidOperationException("Release torrent identity changed; download stopped for review.");
                if (book.FailedReleases.Any(f=>f.Blocked && SameRelease(f,book.SelectedRelease!,prepared.Hash)))
                {
                    var alias=book.SelectedRelease!;
                    book.FailedReleases.Add(new(alias.Guid,alias.IndexerId,prepared.Hash,alias.Title,"Release resolves to a previously blocked torrent.",DateTimeOffset.UtcNow));
                    book.Status="wanted"; book.SelectedRelease=null; book.TorrentHash=null;
                    book.Message="Selected release resolves to a blocked torrent. Waiting for another release.";
                    book.NextSearch=DateTimeOffset.UtcNow.AddMinutes(settings.SearchIntervalMinutes); store.Save(book); return;
                }
                book.TorrentHash = prepared.Hash; store.Save(book); // Persist before remote mutation for restart recovery.
                if ((await qbit.Info(prepared.Hash,ct)).Length == 0) await qbit.Add(settings,book,prepared.Torrent,prepared.Magnet,ct);
            }
            book.Status = "downloading"; book.Message = "Sent to qBittorrent"; store.Save(book); store.Log(book,book.Message);
        }
        if(book.Status == "downloading")
        {
            using var qbit = await integrations.Qbittorrent(settings,ct);
            var torrents = await qbit.Info(book.TorrentHash!,ct);
            if(torrents.Length == 0)
            {
                book.MissingSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow-book.MissingSince >= TimeSpan.FromMinutes(2)) FailDownload(book,"Torrent is missing from qBittorrent.");
                else { book.Message="Waiting for qBittorrent to report the torrent (up to two minutes)."; store.Save(book); }
                return;
            }
            book.MissingSince=null;
            var torrent = torrents[0]; var state = Integrations.Text(torrent,"state");
            if(state is "error" or "missingFiles") { FailDownload(book,"qBittorrent reports " + state + ". Check storage and the download client before replacing this release."); return; }
            var progress = torrent.GetProperty("progress").GetDouble();
            var pollSeconds=int.TryParse(configuration?["GRIMARR_POLL_SECONDS"],out var interval) ? Math.Clamp(interval,1,300) : 15;
            if (ObserveStall(book,state,progress,DateTimeOffset.UtcNow,settings.StallTimeoutMinutes,Math.Max(120,pollSeconds*2)))
            { FailDownload(book,$"No download progress for {settings.StallTimeoutMinutes} minutes."); return; }
            book.Progress = progress;
            book.Message = "qBittorrent: " + state;
            var complete = book.Progress >= 1 && torrent.TryGetProperty("amount_left",out var left) && left.GetInt64() == 0 && !state.StartsWith("checking") && state is not ("moving" or "metaDL" or "allocating");
            if(!complete) { store.Save(book); return; }
            book.DownloadPath=Integrations.Text(torrent,"content_path");
            book.Status="import-pending"; book.Message = "Validating and importing audio"; store.Save(book);
        }
        if(book.Status == "import-pending")
        {
            try { book.ImportedPath = await importer.Import(settings,book,book.DownloadPath!,ct); }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex)
            {
                book.Status="import-failed"; book.Message="Import failed: " + SafeError(ex) + " Fix the issue and retry import.";
                store.Save(book); store.Log(book,book.Message); return;
            }
            book.Status = "scan-pending"; book.Message = "Imported; waiting for Audiobookshelf scan"; store.Save(book); store.Log(book,book.Message);
        }
        if(book.Status == "scan-pending")
        {
            await integrations.Scan(settings,ct);
            book.Status = "available"; book.Message = "Imported. Audiobookshelf scan requested."; book.Progress = 1; store.Save(book); store.Log(book,book.Message);
        }
    }
    private void FailDownload(Book book, string reason)
    {
        book.Status="download-failed"; book.FailureReason=reason;
        book.Message=reason + " Retry this release or search for a replacement. Torrents and files are untouched.";
        if (book.SelectedRelease is {} release && !book.FailedReleases.Any(f=>f.Blocked && SameRelease(f,release,book.TorrentHash)))
            book.FailedReleases.Add(new(release.Guid,release.IndexerId,book.TorrentHash ?? release.InfoHash,release.Title,reason,DateTimeOffset.UtcNow));
        book.NextSearch=DateTimeOffset.MinValue; store.Save(book); store.Log(book,book.Message);
    }
    // Count only continuously observed active download time; pauses, outages, and downtime reset the timer.
    public static bool ObserveStall(Book book, string state, double progress, DateTimeOffset now, int timeoutMinutes, int maximumGapSeconds = 120)
    {
        var active = state is "downloading" or "stalledDL" or "metaDL" or "forcedDL" or "forcedMetaDL";
        if (!active || progress > book.Progress || book.LastDownloadCheck == null || now-book.LastDownloadCheck > TimeSpan.FromSeconds(maximumGapSeconds)) book.LastProgressAt=now;
        book.LastProgressAt ??= now; book.LastDownloadCheck=now;
        return active && progress < 1 && timeoutMinutes > 0 && now-book.LastProgressAt >= TimeSpan.FromMinutes(timeoutMinutes);
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
