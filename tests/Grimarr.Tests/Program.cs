using Grimarr;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

int passed = 0;
void Check(string name, bool condition) { if(!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
void Throws(string name, Action action) { try { action(); } catch(InvalidOperationException) { Check(name,true); return; } throw new Exception("FAIL: " + name); }
var book = new Book { Title="The Hobbit", Author="J R R Tolkien" };
var release = new Release { Title="J R R Tolkien - The Hobbit [English] [Unabridged] M4B", Seeders=12, Size=500_000_000 };
Check("Exact English unabridged match accepted",Ranking.Rank(book,release).Eligible);
Check("Wrong title rejected",!Ranking.Rank(book,release with { Title="Tolkien - The Silmarillion English unabridged" }).Eligible);
Check("Wrong author rejected",!Ranking.Rank(book,release with { Title="Someone Else - The Hobbit English unabridged" }).Eligible);
Check("Unknown language rejected",!Ranking.Rank(book,release with { Title=release.Title.Replace("[English]","") }).Eligible);
Check("Explicit foreign language rejected",!Ranking.Rank(book,release with { Language="de" }).Eligible);
Check("Unknown edition length rejected",!Ranking.Rank(book,release with { Title=release.Title.Replace("[Unabridged]","") }).Eligible);
Check("Either length accepts unknown",Ranking.Rank(book with { Preferences=new(Abridgement:"either") },release with { Title=release.Title.Replace("[Unabridged]","") }).Eligible);
Check("Conflicting abridgement rejected",!Ranking.Rank(book,release with { Title=release.Title+" abridged" }).Eligible);
Check("Standard excludes dramatization",!Ranking.Rank(book with { Preferences=new(Narration:"standard") },release with { Title=release.Title+" GraphicAudio" }).Eligible);
Check("Dramatized requires evidence",!Ranking.Rank(book with { Preferences=new(Narration:"dramatized") },release).Eligible);
Check("Full cast selected",Ranking.Rank(book with { Preferences=new(Narration:"dramatized") },release with { Title=release.Title+" full-cast" }).Eligible);
Check("Dead release rejected",!Ranking.Rank(book,release with { Seeders=0 }).Eligible);
Check("Usenet rejected",!Ranking.Rank(book,release with { Protocol="usenet" }).Eligible);
Check("Pack rejected",!Ranking.Rank(book,release with { Title=release.Title+" collection" }).Eligible);
using (var nullFields=JsonDocument.Parse("{\"seeders\":null}")) Check("Nullable indexer numeric fields tolerated", Integrations.Number(nullFields.RootElement,"seeders")==0);
Check("Rating source required",Ranking.Rank(book,release with { ListenerRating=5,RatingCount=1000 }).Score==Ranking.Rank(book,release).Score);
Check("Verified rating contributes",Ranking.Rank(book,release with { ListenerRating=5,RatingCount=1000,RatingSource="fixture" }).Score>Ranking.Rank(book,release).Score);
Check("Small rating sample weighted down",Ranking.Rank(book,release with { ListenerRating=5,RatingCount=1,RatingSource="fixture" }).Score<Ranking.Rank(book,release with { ListenerRating=4.5,RatingCount=100,RatingSource="fixture" }).Score);
var hash=new string('a',40);
Check("Magnet hash normalized",TorrentIdentity.MagnetHash("magnet:?xt=urn:btih:"+hash.ToUpper()+"&dn=book")==hash);
Check("Base32 magnet supported",TorrentIdentity.MagnetHash("magnet:?xt=urn:btih:"+new string('A',32))==new string('0',40));
Throws("Malformed magnet rejected",()=>TorrentIdentity.MagnetHash("magnet:?xt=urn:btih:bad"));
var info="d4:name4:book6:pieces20:abcdefghijklmnopqrste";
var torrent=Encoding.ASCII.GetBytes("d4:info"+info+"e");
Check("Exact info bytes determine torrent hash",TorrentIdentity.Hash(torrent)==Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes(info))));
Throws("HTML download rejected",()=>TorrentIdentity.Hash(Encoding.UTF8.GetBytes("<html>error</html>")));
Throws("Truncated bencode rejected",()=>TorrentIdentity.Hash(Encoding.ASCII.GetBytes("d4:info100:abc")));
var root=Path.Combine(Path.GetTempPath(),"grimarr-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    var downloads=Path.Combine(root,"downloads");var library=Path.Combine(root,"library");Directory.CreateDirectory(downloads);Directory.CreateDirectory(library);
    var settings=new Settings { DownloadRoot=downloads,LibraryRoot=library,PathMappings=[new("/remote/downloads",downloads)] };
    Check("Remote mapping translates path",Importer.MapPath(settings,"/remote/downloads/book/file.m4b")==Path.Combine(downloads,"book","file.m4b"));
    Throws("Traversal rejected",()=>Importer.MapPath(settings,"/remote/downloads/../../escape"));
    Throws("Sibling prefix rejected",()=>Importer.Under(downloads,downloads+"-other/book"));
    Throws("Download root itself rejected",()=>Importer.Under(downloads,downloads));
    var provider=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root,"keys")));
    var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["GRIMARR_CONFIG"]=root }).Build();
    var store=new Store(config,provider);store.Save(settings with { Prowlarr=new("http://localhost:9696","fixture-secret") });store.Save(book with { SelectedRelease=release with { DownloadUrl="http://localhost/download?apikey=private-download-key" } });
    var reopened=new Store(config,provider);
    Check("Books survive store restart",reopened.Books().Single().Id==book.Id);
    Check("Encrypted secrets survive store restart",reopened.Settings().Prowlarr.ApiKey=="fixture-secret");
    Check("Encrypted download URLs survive restart",reopened.Books().Single().SelectedRelease!.DownloadUrl!.Contains("private-download-key"));
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root,"grimarr.db")}")) {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM settings";Check("API secret not stored in plaintext",!((string)cmd.ExecuteScalar()!).Contains("fixture-secret"));}
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root,"grimarr.db")}")) {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM books";Check("Download credential not stored in plaintext",!((string)cmd.ExecuteScalar()!).Contains("private-download-key"));}
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
Console.WriteLine($"\n{passed} checks passed.");
