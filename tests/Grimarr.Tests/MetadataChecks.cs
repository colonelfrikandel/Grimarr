using Grimarr;
using System.Net;
using System.Text.Json;

static class MetadataChecks
{
    public static async Task Run(Action<string,bool> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"grimarr-metadata-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var downloads=Path.Combine(root,"downloads");var library=Path.Combine(root,"library");Directory.CreateDirectory(downloads);Directory.CreateDirectory(library);
            var source=Path.Combine(downloads,"book");Directory.CreateDirectory(source);
            var frame=new byte[417];new byte[] { 0xff,0xfb,0x90,0x64 }.CopyTo(frame,0);var audio=Enumerable.Range(0,60).SelectMany(_=>frame).ToArray();await File.WriteAllBytesAsync(Path.Combine(source,"book.mp3"),audio);
            var originalCover=new byte[] { 1,2,3,4 };await File.WriteAllBytesAsync(Path.Combine(source,"cover.png"),originalCover);
            const string raw="""{"collectionId":123,"genres":["Fiction","Adventure","fiction"],"collectionExplicitness":"explicit","copyright":"Original copyright","currency":"USD","collectionPrice":12.95,"publisher":"Known publisher","narrators":["Known narrator"],"isbn":"Known ISBN"}""";
            var book=new Book { Title="Reviewed title",Author="Reviewed author",CatalogId="itunes:123",CatalogUrl="https://books.apple.com/us/audiobook/id123",Cover="https://is1-ssl.mzstatic.com/image/thumb/cover/600x600bb.jpg",Description="Selected description",PublishedDate="2020-01-02",Genre="Fiction",AppleMetadataJson=raw,TorrentHash=new string('a',40) };
            var settings=new Settings { DownloadRoot=downloads,LibraryRoot=library,ImportMode="hardlink-or-copy" };
            var handler=new ArtworkHandler();using var client=new HttpClient(handler);var importer=new Importer(client);
            handler.Status=HttpStatusCode.ServiceUnavailable;
            try { await importer.Import(settings,book,source,default);throw new Exception("Expected cover failure"); }
            catch(InvalidOperationException ex) { check("Cover outage leaves a retryable unpublished import",ex.Message.Contains("Retry import") && !Directory.EnumerateDirectories(library).Any()); }
            check("Cover failure leaves downloaded audio and artwork untouched",(await File.ReadAllBytesAsync(Path.Combine(source,"book.mp3"))).SequenceEqual(audio) && (await File.ReadAllBytesAsync(Path.Combine(source,"cover.png"))).SequenceEqual(originalCover));
            handler.Status=HttpStatusCode.OK;
            var final=await importer.Import(settings,book,source,default);
            check("Retry imports the selected cover instead of the download artwork",(await File.ReadAllBytesAsync(Path.Combine(final,"cover.png"))).SequenceEqual(handler.Image) && !File.Exists(Path.Combine(final,"cover.jpg")));
            check("Selected artwork never overwrites a source hardlink",(await File.ReadAllBytesAsync(Path.Combine(source,"cover.png"))).SequenceEqual(originalCover) && (await File.ReadAllBytesAsync(Path.Combine(source,"book.mp3"))).SequenceEqual(audio));
            using var metadata=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(final,"metadata.json")));var m=metadata.RootElement;
            check("Audiobookshelf sidecar preserves reviewed title and author",m.GetProperty("title").GetString()==book.Title && m.GetProperty("authors")[0].GetString()==book.Author);
            check("Audiobookshelf sidecar includes description and full publication date",m.GetProperty("description").GetString()==book.Description && m.GetProperty("publishedYear").GetString()=="2020" && m.GetProperty("publishedDate").GetString()==book.PublishedDate);
            check("Apple genres become deduplicated genres and tags",m.GetProperty("genres").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[] { "Fiction","Adventure" }) && m.GetProperty("tags").GetArrayLength()==2);
            check("Structured Apple metadata maps to supported ABS fields",m.GetProperty("explicit").GetBoolean() && m.GetProperty("publisher").GetString()=="Known publisher" && m.GetProperty("narrators")[0].GetString()=="Known narrator" && m.GetProperty("isbn").GetString()=="Known ISBN");
            check("Unknown language and abridgement are not invented",!m.TryGetProperty("language",out _) && !m.TryGetProperty("abridged",out _));
            using var archive=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(final,"apple-books.json")));
            check("Full selected Apple record is archived without losing unsupported fields",archive.RootElement.GetProperty("metadata").GetProperty("copyright").GetString()=="Original copyright" && archive.RootElement.GetProperty("metadata").GetProperty("collectionPrice").GetDecimal()==12.95m && archive.RootElement.GetProperty("reviewedTitle").GetString()==book.Title);
            var requests=handler.Requests;check("Completed atomic import recovers without refetching artwork",await importer.Import(settings,book,source,default)==final && handler.Requests==requests);
            async Task Reject(string label,Book rejected)
            {
                try { await importer.Import(settings,rejected,source,default);throw new Exception("Expected rejection"); }
                catch(InvalidOperationException) { check(label,!Directory.Exists(Path.Combine(library,rejected.Author,rejected.Title+" ["+rejected.Id[..8]+"]"))); }
            }
            await Reject("Non-Apple cover URLs cannot cause server requests",book with { Id=Guid.NewGuid().ToString("N"),Cover="https://localhost/private" });check("Rejected cover does not contact a different server",handler.Requests==requests);
            handler.Image=System.Text.Encoding.UTF8.GetBytes("<html>not an image</html>");await Reject("HTML cover responses never reach the library",book with { Id=Guid.NewGuid().ToString("N") });
            handler.Status=HttpStatusCode.Redirect;await Reject("Artwork redirects are rejected",book with { Id=Guid.NewGuid().ToString("N") });
            handler.Status=HttpStatusCode.OK;handler.Image=new byte[10*1024*1024+1];await Reject("Oversized cover responses never reach the library",book with { Id=Guid.NewGuid().ToString("N") });
            using var canceled=new CancellationTokenSource();canceled.Cancel();
            try { await importer.Import(settings,book with { Id=Guid.NewGuid().ToString("N") },source,canceled.Token);throw new Exception("Expected cancellation"); }
            catch(OperationCanceledException) { check("Metadata import honors shutdown cancellation",true); }
        }
        finally { Directory.Delete(root,true); }
    }
    sealed class ArtworkHandler:HttpMessageHandler
    {
        public byte[] Image=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7MsAAAAASUVORK5CYII=");
        public HttpStatusCode Status=HttpStatusCode.OK;public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Requests++;return Task.FromResult(new HttpResponseMessage(Status) { Content=new ByteArrayContent(Image) }); }
    }
}
