namespace Grimarr;

public sealed class Workflow(Store store, Integrations integrations, Importer importer)
{
    public SemaphoreSlim Gate { get; } = new(1,1);
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
