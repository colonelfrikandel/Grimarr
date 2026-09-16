namespace Grimarr;

public record Connection(string Url = "", string ApiKey = "", string Username = "", string Password = "");
public record PathMapping(string Remote, string Local);
public record Preferences(string Narration = "either", string Abridgement = "unabridged", int MinimumSeeders = 1, bool PreferM4b = true);
public record Settings
{
    public Connection Prowlarr { get; init; } = new("http://prowlarr:9696");
    public Connection Qbittorrent { get; init; } = new("http://qbittorrent:8080", Username: "admin");
    public Connection Audiobookshelf { get; init; } = new("http://audiobookshelf:80");
    public string LibraryId { get; init; } = "";
    public string LibraryRoot { get; init; } = "/data/audiobooks";
    public string DownloadRoot { get; init; } = "/data/downloads";
    public string QbitSavePath { get; init; } = "";
    public string ImportMode { get; init; } = "hardlink-or-copy";
    public PathMapping[] PathMappings { get; init; } = [];
    public int[] IndexerIds { get; init; } = [];
    public Preferences Defaults { get; init; } = new();
    public int SearchIntervalMinutes { get; init; } = 360;
    public bool AutomationEnabled { get; init; }
}
public record Book
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Cover { get; init; } = "";
    public string CatalogId { get; init; } = "";
    public Preferences Preferences { get; init; } = new();
    public string Status { get; set; } = "wanted";
    public string Message { get; set; } = "Waiting for search";
    public double Progress { get; set; }
    public bool Suspended { get; set; }
    public string? TorrentHash { get; set; }
    public Release? SelectedRelease { get; set; }
    public string? ImportedPath { get; set; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextSearch { get; set; } = DateTimeOffset.MinValue;
}
public record Release
{
    public string Guid { get; init; } = "";
    public string Title { get; init; } = "";
    public string Indexer { get; init; } = "";
    public int IndexerId { get; init; }
    public string Protocol { get; init; } = "torrent";
    public string? DownloadUrl { get; init; }
    public string? MagnetUrl { get; init; }
    public string? InfoHash { get; init; }
    public int Seeders { get; init; }
    public long Size { get; init; }
    public string? Language { get; init; }
    public string? Narration { get; init; }
    public string? Abridgement { get; init; }
    // Only populated by a provider that identifies the exact recording and rating source.
    public double? ListenerRating { get; init; }
    public int? RatingCount { get; init; }
    public string? RatingSource { get; init; }
}
public record RankedRelease(Release Release, double Score, bool Eligible, string[] Reasons);
public record Activity(long Id, DateTimeOffset At, string BookId, string Title, string Message);
