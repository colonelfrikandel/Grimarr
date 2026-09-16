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
var numbered = new Book { Title = "Dungeon Crawler Carl Book 3", Author = "Matt Dinniman", Preferences = new(MinimumSeeders: 0) };
var numberedRelease = new Release { Title = "Dungeon Crawler Carl Season 3 (Book 3) Full Season - Matt Dinniman Audio Immersion Tunnel", Language = "en", Abridgement = "unabridged", SeedersKnown = false, Size = 500000 };
Check("Unknown seeders allowed only with zero minimum",Ranking.Rank(numbered,numberedRelease).Eligible);
Check("Unknown seeders cannot satisfy verified minimum",!Ranking.Rank(numbered with { Preferences=new(MinimumSeeders:1) },numberedRelease).Eligible);
Check("Book two episode three is not book three",!Ranking.Rank(numbered,numberedRelease with { Title="Dungeon Crawler Carl (Book 2) Episode 3 - Matt Dinniman" }).Eligible);
Check("Individual episode is not a whole book",!Ranking.Rank(numbered,numberedRelease with { Title=numberedRelease.Title+" Episode 1" }).Eligible);
Check("Episode range does not prove a complete book",!Ranking.Rank(numbered,numberedRelease with { Title=numberedRelease.Title+" Episodes 1-10" }).Eligible);
Check("Audio Immersion Tunnel is dramatized",!Ranking.Rank(numbered with { Preferences=new(Narration:"standard",MinimumSeeders:0) },numberedRelease).Eligible);
Check("Unconfirmed metadata cannot be selected",!Ranking.Rank(numbered,numberedRelease with { MetadataError="Details unavailable" }).Eligible);
Check("ABB domain lookalike rejected",!AudiobookBay.IsDetailUrl("https://audiobookbay.lu.example.com/abss/book/"));
Check("ABB non-HTTPS rejected",!AudiobookBay.IsDetailUrl("http://audiobookbay.lu/abss/book/"));
Check("ABB credential URL rejected",!AudiobookBay.IsDetailUrl("https://user@audiobookbay.lu/abss/book/"));
Check("ABB private-port URL rejected",!AudiobookBay.IsDetailUrl("https://audiobookbay.lu:8080/abss/book/"));
Check("ABB unrelated page rejected",!AudiobookBay.IsDetailUrl("https://audiobookbay.lu/forum/"));
var detailHtml="""
<div class="postTitle"><h1>The Hobbit - J R R Tolkien</h1></div>
<div class="postInfo"><span itemprop="inLanguage">English</span></div>
<div class="postContent"><div class="desc"><span class="is_abridged">Unabridged</span></div>
<table><tr><td>Info Hash:</td><td>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</td></tr></table></div>
<aside>German Abridged Full Cast</aside>
""";
var detail=AudiobookBay.ParseDetails(detailHtml);
Check("ABB structured metadata ignores sidebar",detail.Language=="english" && detail.Abridgement=="unabridged" && detail.Narration==null);
var enriched=AudiobookBay.Apply(release,detail);
Check("ABB public hash creates validated magnet",TorrentIdentity.MagnetHash(enriched.MagnetUrl!)==detail.Hash && !enriched.SeedersKnown);
Check("ABB mismatched detail page rejected",AudiobookBay.Apply(release,detail with { Title="Unrelated Book" }).MetadataError!=null);
Check("ABB missing language never inferred from sidebar",AudiobookBay.ParseDetails(detailHtml.Replace("<span itemprop=\"inLanguage\">English</span>","")).Language==null);
Check("ABB conflicting languages rejected",AudiobookBay.ParseDetails(detailHtml.Replace("English</span>","English</span><span itemprop=\"inLanguage\">French</span>")).Language=="conflicting");
Throws("ABB malformed torrent hash rejected",()=>AudiobookBay.ParseDetails(detailHtml.Replace(new string('a',40),"bad")));
Throws("ABB missing page title rejected",()=>AudiobookBay.ParseDetails(detailHtml.Replace("<h1>The Hobbit - J R R Tolkien</h1>","")));
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
    var removable = new Book { Title="No results", Author="Fixture", Suspended=true };
    reopened.Save(removable); reopened.Remove(removable);
    Check("Removed waiting book stays removed after restart",!new Store(config,provider).Books().Any(b=>b.Id==removable.Id));
    Check("Removal preserves other books",reopened.Books().Any(b=>b.Id==book.Id));
    Check("Removal retains activity history",reopened.Activities().Any(a=>a.BookId==removable.Id && a.Message=="Removed from Grimarr"));
    reopened.Save(removable); Check("Removed book can be added again",reopened.Books().Any(b=>b.Id==removable.Id)); reopened.Remove(removable);
    Check("Queued book without torrent can be removed",(removable with { Status="queued", SelectedRelease=release }).CanRemove);
    Throws("Queued torrent identity prevents removal",()=>reopened.Remove(removable with { Status="queued", TorrentHash=new string('a',40) }));
    Throws("Downloading book cannot be removed",()=>reopened.Remove(removable with { Status="downloading" }));
    Throws("Imported book cannot be removed",()=>reopened.Remove(removable with { ImportedPath="/data/audiobooks/fixture" }));
    Check("Encrypted secrets survive store restart",reopened.Settings().Prowlarr.ApiKey=="fixture-secret");
    Check("Encrypted download URLs survive restart",reopened.Books().Single().SelectedRelease!.DownloadUrl!.Contains("private-download-key"));
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root,"grimarr.db")}")) {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM settings";Check("API secret not stored in plaintext",!((string)cmd.ExecuteScalar()!).Contains("fixture-secret"));}
    using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root,"grimarr.db")}")) {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM books";Check("Download credential not stored in plaintext",!((string)cmd.ExecuteScalar()!).Contains("private-download-key"));}
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
Console.WriteLine($"\n{passed} checks passed.");
