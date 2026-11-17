using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Grimarr;

public record QuickAddInput(string? Title, string? Author, string? Source = null, string? TorrentBase64 = null);

public static class QuickAdd
{
    public const int MaximumTorrentBytes = 16 * 1024 * 1024;
    public static Book Create(QuickAddInput input, Preferences preferences)
    {
        var title=input.Title?.Trim(); var author=input.Author?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length>250 || string.IsNullOrWhiteSpace(author) || author.Length>200)
            throw new InvalidOperationException("Enter a title and author (maximum 250 and 200 characters).");
        var source=input.Source?.Trim(); var file=input.TorrentBase64;
        if (string.IsNullOrEmpty(source)==string.IsNullOrEmpty(file)) throw new InvalidOperationException("Provide either an info hash/magnet link or a torrent file.");
        string hash; string? magnet=null; string? data=null; long size=0;
        if (!string.IsNullOrEmpty(file))
        {
            if (file.Length>((MaximumTorrentBytes+2)/3)*4) throw new InvalidOperationException("Torrent files must be 16 MB or smaller.");
            byte[] bytes;
            try { bytes=Convert.FromBase64String(file); }
            catch(FormatException) { throw new InvalidOperationException("Upload a valid .torrent file."); }
            if (bytes.Length>MaximumTorrentBytes) throw new InvalidOperationException("Torrent files must be 16 MB or smaller.");
            (hash,size)=TorrentIdentity.ValidateFile(bytes); data=Convert.ToBase64String(bytes);
        }
        else
        {
            if (source!.Length>16384) throw new InvalidOperationException("The magnet link is too long (maximum 16384 characters).");
            if (Regex.IsMatch(source,"^(?:[a-fA-F0-9]{40}|[a-zA-Z2-7]{32})$")) source="magnet:?xt=urn:btih:"+source;
            if (!source.StartsWith("magnet:?",StringComparison.OrdinalIgnoreCase) || source.Contains('#') || source.Any(char.IsControl))
                throw new InvalidOperationException("Enter a v1 info hash or a magnet link, or upload a .torrent file. Web download URLs are not supported here.");
            var query=QueryHelpers.ParseQuery(source[7..]);
            query.TryGetValue("xt",out var xt);
            var identities=xt.Where(v=>v?.StartsWith("urn:btih:",StringComparison.OrdinalIgnoreCase)==true).ToArray();
            if (identities.Length!=1) throw new InvalidOperationException("Provide a magnet with exactly one v1 BitTorrent identity. Pure v2 magnets are not supported.");
            hash=TorrentIdentity.MagnetHash("magnet:?xt="+identities[0]);
            // Keep tracker and other magnet parameters; they may contain private credentials.
            magnet="magnet:"+source[7..];
        }
        return new Book { Title=title, Author=author, Preferences=preferences, AcquisitionMode="quick-add", Status="queued",
            Message="Quick Add release queued for download", SelectedRelease=new Release { Guid="quick:"+hash, Title=title,
                Indexer="Quick Add", InfoHash=hash, MagnetUrl=magnet, TorrentData=data, Size=size, SeedersKnown=false } };
    }
}
