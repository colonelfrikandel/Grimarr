using Grimarr;
using System.Text.Json;

static class ImportChecks
{
    public static async Task Run(Action<string,bool> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"grimarr-import-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var downloads=Path.Combine(root,"downloads"); var library=Path.Combine(root,"library");
            Directory.CreateDirectory(downloads); Directory.CreateDirectory(library);
            var source=Path.Combine(downloads,"book"); Directory.CreateDirectory(source);
            var frame=new byte[417]; new byte[] { 0xff,0xfb,0x90,0x64 }.CopyTo(frame,0);
            var audio=Enumerable.Range(0,60).SelectMany(_=>frame).ToArray();
            var sourceAudio=Path.Combine(source,"book.mp3"); await File.WriteAllBytesAsync(sourceAudio,audio);
            var settings=new Settings { DownloadRoot=downloads,LibraryRoot=library,ImportMode="copy" };
            var book=new Book { Title="Fixture",Author="Author",TorrentHash=new string('a',40) };
            var importer=new Importer();
            var stage=Path.Combine(root,".grimarr-staging",book.Id); Directory.CreateDirectory(stage);
            await File.WriteAllTextAsync(Path.Combine(stage,"stale.mp3"),"partial earlier attempt");
            // Force an I/O failure after staging, where the destination's author directory should be.
            await File.WriteAllTextAsync(Path.Combine(library,"Author"),"collision");
            try { await importer.Import(settings,book,source,CancellationToken.None); throw new Exception("Expected I/O failure"); }
            catch(IOException) { check("Late import failure preserves original audio",(await File.ReadAllBytesAsync(sourceAudio)).SequenceEqual(audio)); }
            check("Retry staging drops stale files",!File.Exists(Path.Combine(stage,"stale.mp3")));
            File.Delete(Path.Combine(library,"Author"));
            var imported=await new Importer().Import(settings,book,source,CancellationToken.None);
            check("Import succeeds after storage issue is fixed",File.Exists(Path.Combine(imported,"book.mp3")));
            check("Import leaves source unchanged",(await File.ReadAllBytesAsync(sourceAudio)).SequenceEqual(audio));
            check("Atomic import exposes only completed files",!Directory.Exists(stage) && !File.Exists(Path.Combine(imported,"stale.mp3")));
            check("Restart after atomic move recovers same destination",await new Importer().Import(settings,book,source,CancellationToken.None)==imported);
            try { await importer.Import(settings,book with { TorrentHash=new string('b',40) },source,CancellationToken.None); throw new Exception("Expected ownership rejection"); }
            catch(InvalidOperationException) { check("Existing import with different torrent is rejected",true); }
            await File.WriteAllTextAsync(Path.Combine(imported,".grimarr.json"),JsonSerializer.Serialize(new { id="other",note=book.Id,torrentHash=book.TorrentHash }));
            try { await importer.Import(settings,book,source,CancellationToken.None); throw new Exception("Expected ownership rejection"); }
            catch(InvalidOperationException) { check("Manifest substring is not proof of ownership",true); }
            var invalid=new Book { Title="Invalid",Author="Author" };
            await File.WriteAllTextAsync(sourceAudio,"not audio");
            try { await importer.Import(settings,invalid,source,CancellationToken.None); throw new Exception("Expected invalid audio rejection"); }
            catch(InvalidOperationException) { check("Invalid audio never reaches library",!Directory.Exists(Path.Combine(library,"Author","Invalid ["+invalid.Id[..8]+"]"))); }
            check("Invalid source is preserved for inspection",File.Exists(sourceAudio));
            using var canceled=new CancellationTokenSource(); canceled.Cancel();
            try { await importer.Import(settings,new Book { Title="Canceled",Author="Author" },source,canceled.Token); throw new Exception("Expected cancellation"); }
            catch(OperationCanceledException) { check("Interrupted import honors shutdown cancellation",true); }
        }
        finally { Directory.Delete(root,true); }
    }
}
