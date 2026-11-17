using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Grimarr;

public record LibraryBook(string Id,string Title,string[] Authors,string RelPath,string Path,bool HasAudio);
public static class LibraryAvailability
{
    public static bool ValidRemoteRoot(string value)
    {
        var path=value.Replace('\\','/');
        return value.Length==0 || (value.Length<=4096 && !value.Any(char.IsControl) && (path.StartsWith('/') || Regex.IsMatch(path,@"^[A-Za-z]:/")) && !path.Split('/').Any(p=>p is "." or ".."));
    }
    public static string Normalize(string value)=>Regex.Replace(new string(value.Normalize(NormalizationForm.FormD).Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant(),@"[^\p{L}\p{N}]","");
    private static string MainTitle(string value)=>Normalize(value.Split(':',2)[0]);
    public static LibraryMatch[] Matches(Book book,IEnumerable<LibraryBook> items)=>items.Where(b=>b.HasAudio && (Normalize(b.Title)==Normalize(book.Title) || (MainTitle(b.Title).Length>=5 && MainTitle(b.Title)==MainTitle(book.Title))))
        .Select(b=>new LibraryMatch(b.Id,b.Title,string.Join(", ",b.Authors),b.Authors.Any(a=>Normalize(a)==Normalize(book.Author))))
        .OrderByDescending(b=>b.SameAuthor).ThenBy(b=>b.Title).ToArray();
    public static LibraryBook[] Parse(JsonElement results)
    {
        if(results.ValueKind!=JsonValueKind.Array)throw new InvalidOperationException("Audiobookshelf returned an unsupported library response.");
        var books=new List<LibraryBook>();
        foreach(var item in results.EnumerateArray())
        {
            if(!item.TryGetProperty("media",out var media) || !media.TryGetProperty("metadata",out var metadata))throw new InvalidOperationException("Audiobookshelf returned incomplete item metadata.");
            if(Integrations.Text(item,"mediaType") is { Length:>0 } type && type!="book")continue;
            var id=Integrations.Text(item,"id");if(id.Length==0)throw new InvalidOperationException("Audiobookshelf returned an item without an identity.");
            var authors=new List<string>();
            if(metadata.TryGetProperty("authors",out var values) && values.ValueKind==JsonValueKind.Array)
                foreach(var author in values.EnumerateArray()) { var name=author.ValueKind==JsonValueKind.String ? author.GetString()! : Integrations.Text(author,"name");if(name.Length>0)authors.Add(name); }
            if(authors.Count==0 && Integrations.Text(metadata,"authorName") is { Length:>0 } combined)authors.Add(combined);
            var audio=media.TryGetProperty("numAudioFiles",out var count) && count.TryGetInt32(out var number) && number>0;
            audio |= media.TryGetProperty("audioFiles",out var files) && files.ValueKind==JsonValueKind.Array && files.GetArrayLength()>0;
            var missing=item.TryGetProperty("isMissing",out var absent) && absent.ValueKind==JsonValueKind.True;
            var invalid=item.TryGetProperty("isInvalid",out var broken) && broken.ValueKind==JsonValueKind.True;
            books.Add(new(id,Integrations.Text(metadata,"title"),authors.ToArray(),Integrations.Text(item,"relPath").Replace('\\','/'),Integrations.Text(item,"path").Replace('\\','/'),audio && !missing && !invalid));
        }
        return books.ToArray();
    }
    public static LibraryBook[] Imported(Settings settings,Book book,IEnumerable<LibraryBook> items)
        =>ImportedDirectories(settings,book,items).Where(i=>Normalize(i.Title)==Normalize(book.Title) && i.Authors.Any(a=>Normalize(a)==Normalize(book.Author))).ToArray();
    public static LibraryBook[] ImportedDirectories(Settings settings,Book book,IEnumerable<LibraryBook> items)
    {
        if(string.IsNullOrEmpty(book.ImportedPath))return [];
        var relative=Path.GetRelativePath(settings.LibraryRoot,Importer.Under(settings.LibraryRoot,book.ImportedPath)).Replace('\\','/');
        var remote=settings.AudiobookshelfLibraryPath.Replace('\\','/').TrimEnd('/');
        // Match the imported relative directory, never another recording with the same title.
        return items.Where(i=>i.HasAudio && (settings.AudiobookshelfLibraryPath.Length>0 ? i.Path==remote+"/"+relative : i.RelPath==relative)).ToArray();
    }
}
