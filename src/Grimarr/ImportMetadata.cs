using System.Net.Http.Headers;
using System.Text.Json;

namespace Grimarr;

public static class ImportMetadata
{
    private static readonly HttpClient ArtworkClient=new(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(20) };
    public static bool HasAppleCover(Book book)=>book.CatalogId.StartsWith("itunes:",StringComparison.Ordinal) && book.Cover.Length>0;

    public static Dictionary<string,object> Fields(Book book)
    {
        var fields=new Dictionary<string,object> { ["title"]=book.Title,["authors"]=new[] { book.Author } };
        if(book.Description.Length>0)fields["description"]=book.Description;
        if(DateOnly.TryParseExact(book.PublishedDate,"yyyy-MM-dd",out var date)) { fields["publishedDate"]=book.PublishedDate;fields["publishedYear"]=date.Year.ToString(); }
        var genres=new List<string>();var tags=new List<string>();if(book.Genre.Length>0)genres.Add(book.Genre);
        if(book.AppleMetadataJson.Length>0)
        {
            using var json=JsonDocument.Parse(book.AppleMetadataJson);var root=json.RootElement;
            if(root.TryGetProperty("genres",out var list) && list.ValueKind==JsonValueKind.Array)
                genres.AddRange(list.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()!).Where(x=>!string.IsNullOrWhiteSpace(x)));
            if(root.TryGetProperty("tags",out var tagList) && tagList.ValueKind==JsonValueKind.Array)
                tags.AddRange(tagList.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()!).Where(x=>!string.IsNullOrWhiteSpace(x)));
            foreach(var key in new[] { "subtitle","publisher","language","isbn","asin" })
            {
                var value=Integrations.Text(root,key).Trim();if(value.Length>0)fields[key]=value;
            }
            var explicitness=Integrations.Text(root,"collectionExplicitness");
            if(explicitness is "explicit" or "notExplicit" or "cleaned") fields["explicit"]=explicitness=="explicit";
            if(root.TryGetProperty("abridged",out var abridged) && abridged.ValueKind is JsonValueKind.True or JsonValueKind.False)fields["abridged"]=abridged.GetBoolean();
            if(root.TryGetProperty("narrators",out var narrators) && narrators.ValueKind==JsonValueKind.Array)
            {
                var names=narrators.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()!).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
                if(names.Length>0)fields["narrators"]=names;
            }
        }
        var categories=genres.Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(categories.Length>0)fields["genres"]=categories;
        var allTags=categories.Concat(tags.Select(x=>x.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(allTags.Length>0)fields["tags"]=allTags;
        return fields;
    }

    public static async Task Write(string stage,Book book,CancellationToken ct,HttpClient? client=null)
    {
        if(HasAppleCover(book))
        {
            if(!AppleCatalog.IsArtworkUrl(book.Cover))throw new InvalidOperationException("Selected Apple Books artwork URL is unsupported. Review the selected cover.");
            try
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,book.Cover);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response=await (client ?? ArtworkClient).SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                response.EnsureSuccessStatusCode();
                const int maximum=10*1024*1024;
                if(response.Content.Headers.ContentLength>maximum)throw new InvalidOperationException("Selected Apple Books cover exceeds 10 MB.");
                await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);using var buffer=new MemoryStream();var chunk=new byte[81920];int count;
                while((count=await input.ReadAsync(chunk,timeout.Token))>0) { if(buffer.Length+count>maximum)throw new InvalidOperationException("Selected Apple Books cover exceeds 10 MB.");await buffer.WriteAsync(chunk.AsMemory(0,count),timeout.Token); }
                var bytes=buffer.ToArray();
                var jpeg=bytes.Length>16 && bytes[0]==255 && bytes[1]==216 && bytes[2]==255;
                var png=bytes.Length>16 && bytes.AsSpan(0,8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 });
                if(!jpeg && !png)throw new InvalidOperationException("Selected Apple Books cover is not a supported JPEG or PNG image.");
                var target=Path.Combine(stage,png?"cover.png":"cover.jpg");
                // Never overwrite a hardlink to the original download.
                if(File.Exists(target))File.Delete(target);
                await File.WriteAllBytesAsync(target,bytes,ct);
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested) { throw new InvalidOperationException("Selected Apple Books cover download timed out. Retry import to try again."); }
            catch(HttpRequestException) { throw new InvalidOperationException("Selected Apple Books cover could not be downloaded. Retry import to try again."); }
        }
        await File.WriteAllTextAsync(Path.Combine(stage,"metadata.json"),JsonSerializer.Serialize(Fields(book),Store.Json),ct);
        if(book.CatalogId.StartsWith("itunes:",StringComparison.Ordinal))
        {
            using var raw=JsonDocument.Parse(book.AppleMetadataJson.Length>0?book.AppleMetadataJson:"{}");
            var archive=new { source="Apple Books",book.CatalogId,book.CatalogUrl,coverUrl=book.Cover,reviewedTitle=book.Title,reviewedAuthor=book.Author,book.Description,book.PublishedDate,book.Genre,metadata=raw.RootElement };
            await File.WriteAllTextAsync(Path.Combine(stage,"apple-books.json"),JsonSerializer.Serialize(archive,Store.Json),ct);
        }
    }
}
