using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Grimarr;

public record CatalogBook(string Title,string Author,string Cover,string CatalogId,string Description,string ReleaseDate,string Genre,string StoreUrl,string SelectionId,string MetadataJson="")
{
    public string Source => "Apple Books";
    public double? Rating => null;
}

public sealed class AppleCatalog : IDisposable
{
    private readonly HttpClient client;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly ConcurrentDictionary<string,(CatalogBook[] Items,DateTimeOffset Expires)> queries=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,(CatalogBook Book,DateTimeOffset Expires)> choices=new();
    public AppleCatalog(HttpClient? client=null,TimeProvider? clock=null)
    {
        this.client=client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(20),MaxResponseContentBufferSize=8*1024*1024 };
        this.clock=clock ?? TimeProvider.System;
    }
    public async Task<CatalogBook[]> Search(string query,CancellationToken ct)
    {
        query=Regex.Replace(query.Trim(),@"\s+"," ");
        if (query.Length is <2 or >200) throw new InvalidOperationException("Search must contain 2–200 characters.");
        if (queries.TryGetValue(query,out var cached) && cached.Expires>clock.GetUtcNow()) return cached.Items;
        await gate.WaitAsync(ct);
        try
        {
            if (queries.TryGetValue(query,out cached) && cached.Expires>clock.GetUtcNow()) return cached.Items;
            using var response=await client.GetAsync("https://itunes.apple.com/search?media=audiobook&entity=audiobook&country=US&limit=24&term="+Uri.EscapeDataString(query),ct);
            response.EnsureSuccessStatusCode();
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var books=Parse(json.RootElement);
            var now=clock.GetUtcNow();
            foreach(var key in choices.Where(c=>c.Value.Expires<=now).Select(c=>c.Key)) choices.TryRemove(key,out _);
            if (choices.Count+books.Length>2400) { choices.Clear();queries.Clear(); }
            if (queries.Count>=100) queries.Clear();
            foreach(var book in books) choices[book.SelectionId]=(book,now.AddMinutes(15));
            queries[query]=(books,now.AddMinutes(5));return books;
        }
        finally { gate.Release(); }
    }
    public Book Apply(Book book,string? selectionId)
    {
        if (string.IsNullOrWhiteSpace(selectionId)) return book;
        if (!Guid.TryParseExact(selectionId,"N",out _) || !choices.TryGetValue(selectionId,out var choice) || choice.Expires<=clock.GetUtcNow())
            throw new InvalidOperationException("Apple Books choices expired. Search again or clear the Apple Books match to use manual details.");
        var match=choice.Book;
        // Title and author remain the user's reviewed values, including manual corrections.
        return book with { Cover=match.Cover,CatalogId=match.CatalogId,Description=match.Description,PublishedDate=match.ReleaseDate,Genre=match.Genre,CatalogUrl=match.StoreUrl,AppleMetadataJson=match.MetadataJson };
    }
    public static CatalogBook[] Parse(JsonElement root)
    {
        if (!root.TryGetProperty("results",out var results) || results.ValueKind!=JsonValueKind.Array) throw new InvalidOperationException("Apple Books returned an unsupported response. Use manual details or try again.");
        var books=new List<CatalogBook>();var seen=new HashSet<long>();
        foreach(var row in results.EnumerateArray().Take(24))
        {
            if (row.ValueKind!=JsonValueKind.Object || Integrations.Text(row,"wrapperType")!="audiobook" || !row.TryGetProperty("collectionId",out var id) || id.ValueKind!=JsonValueKind.Number || !id.TryGetInt64(out var number) || number<=0 || !seen.Add(number)) continue;
            var title=Text(row,"collectionName",250);var author=Text(row,"artistName",200);
            if (title.Length==0 || author.Length==0) continue;
            var cover=SafeUrl(Integrations.Text(row,"artworkUrl100"),true);
            cover=Regex.Replace(cover,@"(?<=/)\d+x\d+bb(?=[-.])","600x600bb");
            var url=SafeUrl(Integrations.Text(row,"collectionViewUrl"),false);
            var description=Integrations.Text(row,"description");if (description.Length==0) description=Integrations.Text(row,"shortDescription");
            var document=new HtmlParser().ParseDocument(description[..Math.Min(description.Length,50000)]);
            foreach(var node in document.QuerySelectorAll("script,style")) node.Remove();
            description=(document.Body?.TextContent ?? "").Trim();description=description[..Math.Min(description.Length,12000)];
            var date=DateTimeOffset.TryParse(Integrations.Text(row,"releaseDate"),out var releaseDate) ? releaseDate.ToString("yyyy-MM-dd") : "";
            var metadata=row.GetRawText();
            if(metadata.Length>256*1024) throw new InvalidOperationException("Apple Books returned oversized metadata. Try again or use manual details.");
            books.Add(new(title,author,cover,"itunes:"+number,description,date,Text(row,"primaryGenreName",100),url,Guid.NewGuid().ToString("N"),metadata));
        }
        return books.ToArray();
    }
    private static string Text(JsonElement row,string key,int maximum)
    {
        var value=Integrations.Text(row,key).Trim();return value[..Math.Min(value.Length,maximum)];
    }
    public static bool IsArtworkUrl(string value)=>SafeUrl(value,true).Length>0;
    private static string SafeUrl(string value,bool artwork)
    {
        if (!Uri.TryCreate(value,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo.Length>0 || !uri.IsDefaultPort || value.Length>2000) return "";
        return artwork ? (uri.Host.EndsWith(".mzstatic.com",StringComparison.OrdinalIgnoreCase) ? value : "")
            : (uri.Host is "books.apple.com" or "itunes.apple.com" ? value : "");
    }
    public void Dispose() { client.Dispose();gate.Dispose(); }
}
