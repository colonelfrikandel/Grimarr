using Grimarr;
using System.Text;
using System.Security.Cryptography;

static class QuickAddChecks
{
    public static void Run(Action<string,bool> check, Action<string,Action> throws)
    {
        var prefs=new Preferences();var h=new string('a',40);
        var raw=QuickAdd.Create(new(" Title "," Author ",h.ToUpperInvariant()),prefs);
        check("Quick Add normalizes hash and queues exact release",raw.Status=="queued" && raw.AcquisitionMode=="quick-add" && raw.SelectedRelease!.InfoHash==h && raw.Title=="Title" && raw.Author=="Author");
        check("Quick Add preserves defaults and is removable before submission",raw.Preferences==prefs && raw.CanRemove);
        check("Quick Add accepts base32 hash",QuickAdd.Create(new("Title","Author",new string('A',32)),prefs).SelectedRelease!.InfoHash==new string('0',40));
        var magnet="magnet:?xt=urn:btih:"+h+"&tr=https%3A%2F%2Ftracker.example%2Fprivate-key";
        check("Quick Add preserves magnet tracker parameters",QuickAdd.Create(new("Title","Author",magnet),prefs).SelectedRelease!.MagnetUrl==magnet);
        throws("Quick Add rejects HTTP download URLs",()=>QuickAdd.Create(new("Title","Author","http://localhost/private"),prefs));
        throws("Quick Add rejects invalid info hash",()=>QuickAdd.Create(new("Title","Author","bad"),prefs));
        throws("Quick Add requires title and author",()=>QuickAdd.Create(new(" ","Author",h),prefs));
        throws("Quick Add rejects missing source",()=>QuickAdd.Create(new("Title","Author"),prefs));
        throws("Quick Add rejects multiple sources",()=>QuickAdd.Create(new("Title","Author",h,"AAAA"),prefs));
        throws("Quick Add rejects conflicting magnet identities",()=>QuickAdd.Create(new("Title","Author",magnet+"&xt=urn:btih:"+new string('b',40)),prefs));
        throws("Quick Add rejects magnets without v1 identity",()=>QuickAdd.Create(new("Title","Author","magnet:?dn=book"),prefs));
        var info="d6:lengthi4e4:name8:book.mp312:piece lengthi16384e6:pieces20:abcdefghijklmnopqrste";
        var bytes=Encoding.ASCII.GetBytes("d4:info"+info+"e");
        var parsed=TorrentIdentity.ValidateFile(bytes);var expected=Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes(info)));
        check("Uploaded torrent identity uses exact info bytes",parsed.Hash==expected && parsed.Size==4);
        var uploaded=QuickAdd.Create(new("Title","Author",TorrentBase64:Convert.ToBase64String(bytes)),prefs);
        check("Quick Add retains validated torrent bytes for retry",uploaded.SelectedRelease!.TorrentData==Convert.ToBase64String(bytes) && uploaded.SelectedRelease.InfoHash==expected);
        throws("Torrent upload rejects trailing garbage",()=>TorrentIdentity.ValidateFile([..bytes,(byte)'x']));
        throws("Torrent upload rejects duplicate info dictionaries",()=>TorrentIdentity.ValidateFile(Encoding.ASCII.GetBytes("d4:info"+info+"4:info"+info+"e")));
        throws("Torrent upload rejects path traversal",()=>TorrentIdentity.ValidateFile(Encoding.ASCII.GetBytes("d4:info"+info.Replace("8:book.mp3","2:..")+"e")));
        throws("Torrent upload rejects unsafe UTF-8 name override",()=>TorrentIdentity.ValidateFile(Encoding.ASCII.GetBytes("d4:info"+info.Insert(1,"10:name.utf-82:..")+"e")));
        throws("Torrent upload rejects piece count mismatch",()=>TorrentIdentity.ValidateFile(Encoding.ASCII.GetBytes("d4:info"+info.Replace("i4e","i999999e")+"e")));
        throws("Torrent upload rejects malformed dictionaries",()=>TorrentIdentity.ValidateFile(Encoding.ASCII.GetBytes("d4:info"+info+"x")));
        throws("Torrent upload rejects invalid base64",()=>QuickAdd.Create(new("Title","Author",TorrentBase64:"not-base64!"),prefs));
        throws("Torrent upload rejects oversized files before decode",()=>QuickAdd.Create(new("Title","Author",TorrentBase64:new string('A',((QuickAdd.MaximumTorrentBytes+2)/3)*4+1)),prefs));
    }
}
