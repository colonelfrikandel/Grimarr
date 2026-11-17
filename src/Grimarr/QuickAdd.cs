using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Grimarr;

public record QuickAddInput(string? Title = null, string? Author = null, string? Source = null, string? TorrentBase64 = null, string? CatalogSelectionId = null);
public record QuickAddSuggestion(string Title, string Author, string DisplayName, string Notice);

public static class QuickAdd
{
    public const int MaximumTorrentBytes = 16 * 1024 * 1024;
    public static Book Create(QuickAddInput input, Preferences preferences)
    {
        var title=input.Title?.Trim(); var author=input.Author?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length>250 || string.IsNullOrWhiteSpace(author) || author.Length>200)
            throw new InvalidOperationException("Enter a title and author (maximum 250 and 200 characters).");
        var parsed=ReadSource(input);
        return new Book { Title=title, Author=author, Preferences=preferences, AcquisitionMode="quick-add", Status="queued",
            Message="Quick Add release queued for download", SelectedRelease=parsed.Release with { Title=title } };
    }
    public static QuickAddSuggestion Preview(QuickAddInput input) => Suggest(ReadSource(input).Name);
    private static (Release Release,string? Name) ReadSource(QuickAddInput input)
    {
        var source=input.Source?.Trim(); var file=input.TorrentBase64;
        if (string.IsNullOrEmpty(source)==string.IsNullOrEmpty(file)) throw new InvalidOperationException("Provide either an info hash/magnet link or a torrent file.");
        string hash; string? magnet=null; string? data=null; string? name=null; long size=0;
        if (!string.IsNullOrEmpty(file))
        {
            if (file.Length>((MaximumTorrentBytes+2)/3)*4) throw new InvalidOperationException("Torrent files must be 16 MB or smaller.");
            byte[] bytes;
            try { bytes=Convert.FromBase64String(file); }
            catch(FormatException) { throw new InvalidOperationException("Upload a valid .torrent file."); }
            if (bytes.Length>MaximumTorrentBytes) throw new InvalidOperationException("Torrent files must be 16 MB or smaller.");
            (hash,size,name)=TorrentIdentity.InspectFile(bytes); data=Convert.ToBase64String(bytes);
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
            if (query.TryGetValue("dn",out var names)) name=names.FirstOrDefault();
        }
        return (new Release { Guid="quick:"+hash, Indexer="Quick Add", InfoHash=hash, MagnetUrl=magnet, TorrentData=data, Size=size, SeedersKnown=false },name);
    }
    public static QuickAddSuggestion Suggest(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new("","","","No book name is available in this source. Enter the title and author below; a bare info hash contains neither.");
        name=string.Concat(name.Take(512).Where(c=>!char.IsControl(c))).Trim();
        var clean=Regex.Replace(name,@"\.(?:m4b|mp3|m4a|flac|ogg|opus|torrent)$","",RegexOptions.IgnoreCase).Replace('_',' ');
        clean=Regex.Replace(clean,@"[\[(]([^\]\)]*)[\])]",match=>Regex.IsMatch(match.Groups[1].Value,@"^(?:(?:mp3|m4b|m4a|flac|aac|audiobook|english|unabridged|abridged|full.cast|dramatized|\d+\s*k(?:b(?:p?s)?)?|\d+\s*khz|\d+\s*bit)[\s,;/+-]*)+$",RegexOptions.IgnoreCase) ? " " : match.Value);
        clean=Regex.Replace(clean,@"(?:\s*[-–—,]\s*|\s+)(?:narrated|read)\s+by\s+.*$","",RegexOptions.IgnoreCase);
        clean=Regex.Replace(clean,@"\s+"," ").Trim(' ','-','–','—');
        string title=clean,author="",notice="Author could not be identified. Enter it below and check the suggested title.";
        var parts=Regex.Split(clean,@"\s+[-–—]\s+");
        bool TitleLike(string value) => Regex.IsMatch(value,@"\d|\b(?:book|volume|vol|part|series|trilogy|of|and|by|for)\b|^(?:the|a|an)\s|\s[IVX]{2,6}$",RegexOptions.IgnoreCase);
        if (parts.Length==2 && parts.All(p=>!string.IsNullOrWhiteSpace(p)))
        {
            bool titleFirst=TitleLike(parts[0]) && !TitleLike(parts[1]);
            (title,author)=titleFirst ? (parts[0],parts[1]) : (parts[1],parts[0]);
            notice=titleFirst ? "Suggested from a Title – Author name. Review and correct both fields before adding." : "Assumed Author – Title ordering. Review and correct both fields before adding.";
        }
        else if(parts.Length==1)
        {
            var by=Regex.Match(clean,@"^(?<title>.+)\s+by\s+(?<author>.+)$",RegexOptions.IgnoreCase);
            if (by.Success) { title=by.Groups["title"].Value;author=by.Groups["author"].Value;notice="Suggested from the book name. Review and correct both fields before adding."; }
        }
        return new(title[..Math.Min(title.Length,250)].Trim(),author[..Math.Min(author.Length,200)].Trim(),name,notice);
    }
}
