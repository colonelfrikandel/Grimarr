using Grimarr;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;

static class SecurityChecks
{
    public static async Task Run(Action<string,bool> check,Action<string,Action> throws)
    {
        var version=AppSecurity.SessionVersion("fixture-password-one");
        check("Session version is stable across unchanged-password restarts",AppSecurity.ValidSession(version,AppSecurity.SessionVersion("fixture-password-one")));
        check("Password rotation invalidates old session versions",!AppSecurity.ValidSession(version,AppSecurity.SessionVersion("fixture-password-two")));
        check("Missing and malformed session versions are rejected",!AppSecurity.ValidSession(null,version) && !AppSecurity.ValidSession("bad",version));
        var hash=new string('a',40);var magnet="magnet:?xt=urn:btih:"+hash;
        throws("Indexer magnets reject newline-separated extra downloads",()=>TorrentIdentity.MagnetHash(magnet+"\nmagnet:?xt=urn:btih:"+new string('b',40)));
        throws("Indexer magnets reject conflicting identities",()=>TorrentIdentity.MagnetHash(magnet+"&xt=urn:btih:"+new string('b',40)));
        throws("Magnet parsing does not turn encoded delimiters into identities",()=>TorrentIdentity.MagnetHash("magnet:?xt="+Uri.EscapeDataString("urn:btih:"+hash+"&xt=urn:btih:"+new string('b',40))));
        throws("A hash in arbitrary HTTP query text is not a magnet",()=>TorrentIdentity.MagnetHash("https://example.invalid/?xt=urn:btih:"+hash));
        check("Encoded magnet parameters cannot impersonate identity fields",TorrentIdentity.MagnetHash(magnet+"&dn="+Uri.EscapeDataString("A title &xt=urn:btih:"+new string('b',40)))==hash);
        check("Encoded valid identity is decoded exactly once",TorrentIdentity.MagnetHash("magnet:?xt="+Uri.EscapeDataString("urn:btih:"+hash))==hash);
        throws("Torrent metadata cannot request symlink creation",()=>TorrentIdentity.ValidateFile(Encoding.ASCII.GetBytes("d4:infod4:attr1:l6:lengthi4e4:name8:book.mp312:piece lengthi16384e6:pieces20:abcdefghijklmnopqrstee")));
        var root=Path.Combine(Path.GetTempPath(),"grimarr-security-"+Guid.NewGuid().ToString("N"));AppSecurity.PrivateConfigDirectory(root);
        try
        {
            if(!OperatingSystem.IsWindows())check("New Unix config roots are owner-only",(File.GetUnixFileMode(root)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))==0);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["GRIMARR_CONFIG"]=root }).Build();var provider=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root,"keys")));
            var store=new Store(config,provider);const string identifier="https://tracker.example/download?passkey=identifier-secret";
            var book=new Book {Title="Security fixture",Author="Author",SelectedRelease=new Release {Guid=identifier},FailedReleases=[new(identifier,1,null,"Fixture","Fixture",DateTimeOffset.UtcNow,true)]};store.Save(book);
            using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root,"grimarr.db")}"))
            {
                db.Open();using var read=db.CreateCommand();read.CommandText="SELECT json FROM books";var raw=(string)read.ExecuteScalar()!;
                check("Private selected and failed identifiers are encrypted at rest",!raw.Contains("identifier-secret"));
                check("Encrypted identifiers retain retry and blocklist identity",new Store(config,provider).Books().Single().SelectedRelease!.Guid==identifier && new Store(config,provider).Books().Single().FailedReleases[0].Guid==identifier);
                using var legacy=db.CreateCommand();legacy.CommandText="UPDATE books SET json=$json; DELETE FROM security_migrations";legacy.Parameters.AddWithValue("$json",JsonSerializer.Serialize(book,Store.Json));legacy.ExecuteNonQuery();
            }
            var migrated=new Store(config,provider).Books().Single();
            check("Legacy private identifiers migrate without changing workflow state",migrated.Id==book.Id && migrated.Status==book.Status && migrated.SelectedRelease!.Guid==identifier && migrated.FailedReleases[0].Guid==identifier);
            using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root,"grimarr.db")}")) {db.Open();using var read=db.CreateCommand();read.CommandText="SELECT json FROM books";check("Migration removes plaintext private identifiers",!((string)read.ExecuteScalar()!).Contains("identifier-secret"));}
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            check("Migration purges legacy plaintext from database pages",!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(root,"grimarr.db"))).Contains("identifier-secret"));
            if(!OperatingSystem.IsWindows())
            {
                var link=Path.Combine(root,"dangling");File.CreateSymbolicLink(link,Path.Combine(root,"absent"));throws("Dangling symlinks cannot bypass import path checks",()=>Importer.NoLinks(link));File.Delete(link);
            }
            var downloads=Path.Combine(root,"downloads");var library=Path.Combine(root,"library");Directory.CreateDirectory(downloads);Directory.CreateDirectory(library);
            var playlist=Path.Combine(downloads,"playlist.mp3");await File.WriteAllTextAsync(playlist,"ffconcat version 1.0\nfile 'http://127.0.0.1:1/not-allowed.mp3'\n");
            try {await new Importer().Import(new Settings {DownloadRoot=downloads,LibraryRoot=library},new Book {Title="Playlist",Author="Author"},playlist,default);throw new Exception("Expected format rejection");}
            catch(InvalidOperationException) {check("Playlists disguised as audio are rejected before import",!Directory.EnumerateDirectories(library).Any());}
            foreach(var (extension,codec) in new[] {("m4b","aac"),("m4a","aac"),("flac","flac"),("ogg","libvorbis"),("opus","libopus")})
            {
                var file=Path.Combine(downloads,"fixture."+extension);
                var start=new System.Diagnostics.ProcessStartInfo("ffmpeg") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
                foreach(var arg in new[] {"-v","error","-f","lavfi","-i","anullsrc=r=48000:cl=mono","-t","0.2","-c:a",codec,file})start.ArgumentList.Add(arg);
                using var process=System.Diagnostics.Process.Start(start)!;var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
                if(process.ExitCode!=0)throw new Exception("Audio fixture generation failed: "+await error);await error;
                var imported=await new Importer().Import(new Settings {DownloadRoot=downloads,LibraryRoot=library,ImportMode="copy"},new Book {Title="Format "+extension,Author="Author"},file,default);
                var importedBytes=await File.ReadAllBytesAsync(Path.Combine(imported,Path.GetFileName(file)));var sourceBytes=await File.ReadAllBytesAsync(file);
                check("Restricted audio probing retains supported "+extension+" imports",importedBytes.SequenceEqual(sourceBytes));
            }
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
}
