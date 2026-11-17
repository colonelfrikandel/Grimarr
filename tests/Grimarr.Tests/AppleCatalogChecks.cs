using System.Net;
using System.Text.Json;
using Grimarr;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

static class AppleCatalogChecks
{
    public static async Task Run(Action<string,bool> check,Action<string,Action> throws)
    {
        const string response="""
        {"results":[
          {"wrapperType":"audiobook","collectionId":123,"collectionName":"The Book","artistName":"An Author","artworkUrl100":"https://is1-ssl.mzstatic.com/image/thumb/book/100x100bb.jpg","collectionViewUrl":"https://books.apple.com/us/audiobook/id123","description":"<p>A &amp; B</p><script>bad()</script><style>hidden</style>","releaseDate":"2020-01-02T08:00:00Z","primaryGenreName":"Fiction"},
          {"wrapperType":"audiobook","collectionId":124,"collectionName":"Other edition","artistName":"An Author","artworkUrl100":"http://evil.example/cover","collectionViewUrl":"https://evil.example/book","releaseDate":"invalid"},
          {"wrapperType":"audiobook","collectionId":123,"collectionName":"Duplicate","artistName":"An Author"},
          {"wrapperType":"track","collectionId":125,"collectionName":"Not a book","artistName":"Other"},
          {"wrapperType":"audiobook","collectionId":"126","collectionName":"Invalid id","artistName":"Other"},
          {"wrapperType":"audiobook","collectionId":127,"collectionName":"","artistName":"Other"},null]}
        """;
        var handler=new FixtureHandler(response);var clock=new FixtureClock();
        using var catalog=new AppleCatalog(new HttpClient(handler),clock);
        var results=await catalog.Search("  The   Book An Author  ",default);
        check("Apple lookup keeps distinct audiobook editions only",results.Length==2 && results[0].CatalogId=="itunes:123");
        check("Apple lookup includes high resolution cover and edition details",results[0].Cover.EndsWith("600x600bb.jpg") && results[0].ReleaseDate=="2020-01-02" && results[0].Genre=="Fiction");
        check("Apple descriptions strip HTML, scripts and styles",results[0].Description=="A & B");
        check("Untrusted artwork and store links are discarded",results[1].Cover=="" && results[1].StoreUrl=="" && results[1].ReleaseDate=="");
        check("Each returned edition has a separate opaque choice",results.Select(b=>b.SelectionId).Distinct().Count()==2 && results.All(b=>Guid.TryParseExact(b.SelectionId,"N",out _)));
        check("Lookup sends a title query only to the fixed Apple endpoint",handler.Last!.Host=="itunes.apple.com" && handler.Last.Query.Contains("term=The%20Book%20An%20Author") && handler.Last.Query.Contains("entity=audiobook"));
        var cached=await catalog.Search("the book an author",default);
        check("Normalized queries reuse cached choices",handler.Calls==1 && cached[0].SelectionId==results[0].SelectionId);
        var manual=new Book { Title="Corrected title",Author="Corrected author" };
        check("Manual details work without a catalog choice",catalog.Apply(manual,null)==manual);
        var selected=catalog.Apply(manual,results[0].SelectionId);
        check("Explicit selection saves metadata while preserving reviewed names",selected.Title==manual.Title && selected.Author==manual.Author && selected.Cover==results[0].Cover && selected.Description=="A & B" && selected.CatalogUrl==results[0].StoreUrl && selected.PublishedDate=="2020-01-02");
        throws("Fabricated catalog choices are rejected",()=>catalog.Apply(manual,Guid.NewGuid().ToString("N")));
        throws("Malformed catalog choices are rejected",()=>catalog.Apply(manual,"forged"));
        var root=Path.Combine(Path.GetTempPath(),"grimarr-catalog-"+Guid.NewGuid().ToString("N"));
        try {
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["GRIMARR_CONFIG"]=root }).Build();
            var provider=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root,"keys")));
            new Store(config,provider).Save(selected);
            var reopened=new Store(config,provider).Books().Single();
            check("Chosen cover and details survive a store restart",reopened.Cover==selected.Cover && reopened.Description==selected.Description && reopened.CatalogId==selected.CatalogId && reopened.PublishedDate==selected.PublishedDate && reopened.Genre==selected.Genre && reopened.CatalogUrl==selected.CatalogUrl);
        } finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true); }
        clock.Now=clock.Now.AddMinutes(16);
        throws("Expired catalog choice requires a new lookup",()=>catalog.Apply(manual,results[0].SelectionId));
        var refreshed=await catalog.Search("The Book An Author",default);
        check("Expired search refreshes results and choices",handler.Calls==2 && refreshed[0].SelectionId!=results[0].SelectionId);
        using var bad=JsonDocument.Parse("{\"unexpected\":true}");
        throws("Malformed Apple response has a manual fallback error",()=>AppleCatalog.Parse(bad.RootElement));
        using var failed=new AppleCatalog(new HttpClient(new FixtureHandler("unavailable",HttpStatusCode.ServiceUnavailable)));
        var unavailable=false;try { await failed.Search("The Book",default); }catch(HttpRequestException){unavailable=true;}
        check("Apple outages return a failed lookup without losing manual details",unavailable && failed.Apply(manual,null)==manual);
    }
    sealed class FixtureHandler(string response,HttpStatusCode status=HttpStatusCode.OK):HttpMessageHandler
    {
        public int Calls;public Uri? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        { Calls++;Last=request.RequestUri;return Task.FromResult(new HttpResponseMessage(status) { Content=new StringContent(response) }); }
    }
    sealed class FixtureClock:TimeProvider
    { public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now; }
}
