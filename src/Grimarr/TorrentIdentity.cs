using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Grimarr;

public static class TorrentIdentity
{
    public static (string Hash, long Size) ValidateFile(byte[] data)
    {
        if (data.Length==0 || data.Length>QuickAdd.MaximumTorrentBytes) throw new InvalidOperationException("Upload a .torrent file between 1 byte and 16 MB.");
        int cursor=0,nodes=0;
        InvalidOperationException Invalid() => new("Invalid or unsupported torrent metadata.");
        object Read(int depth)
        {
            if (depth>64 || ++nodes>200000 || cursor>=data.Length) throw Invalid();
            var token=data[cursor];
            if (token=='d')
            {
                cursor++; var dict=new Dictionary<string,object>(StringComparer.Ordinal);
                while(cursor<data.Length && data[cursor]!='e')
                {
                    if (Read(depth+1) is not byte[] key) throw Invalid();
                    if (!dict.TryAdd(Encoding.Latin1.GetString(key),Read(depth+1))) throw Invalid();
                }
                if (cursor>=data.Length) throw Invalid(); cursor++; return dict;
            }
            if (token=='l')
            {
                cursor++; var list=new List<object>();
                while(cursor<data.Length && data[cursor]!='e') list.Add(Read(depth+1));
                if (cursor>=data.Length) throw Invalid(); cursor++; return list;
            }
            if (token=='i')
            {
                var start=++cursor; while(cursor<data.Length && data[cursor]!='e') cursor++;
                if (cursor>=data.Length) throw Invalid();
                var text=Encoding.ASCII.GetString(data,start,cursor-start);cursor++;
                if (!Regex.IsMatch(text,@"^(?:0|-?[1-9][0-9]*)$") || !long.TryParse(text,out var number)) throw Invalid();
                return number;
            }
            var begin=cursor; while(cursor<data.Length && data[cursor] is >=(byte)'0' and <=(byte)'9') cursor++;
            if (cursor==begin || cursor>=data.Length || data[cursor]!=':' || (cursor-begin>1 && data[begin]=='0') || !int.TryParse(Encoding.ASCII.GetString(data,begin,cursor-begin),out var length)) throw Invalid();
            cursor++; if (length<0 || length>data.Length-cursor) throw Invalid();
            var bytes=data.AsSpan(cursor,length).ToArray();cursor+=length;return bytes;
        }
        bool Segment(object? value) => value is byte[] bytes && bytes.Length>0 && !bytes.Any(b=>b==0 || b=='/' || b=='\\') && Encoding.Latin1.GetString(bytes) is not ("." or "..");
        if (Read(0) is not Dictionary<string,object> root || cursor!=data.Length || !root.TryGetValue("info",out var raw) || raw is not Dictionary<string,object> info) throw Invalid();
        if (!info.TryGetValue("pieces",out var p) || p is not byte[] pieces || pieces.Length==0 || pieces.Length%20!=0)
            throw new InvalidOperationException("Only v1 or hybrid .torrent files are supported; valid v1 piece hashes are required.");
        if (!info.TryGetValue("piece length",out var pl) || pl is not long pieceLength || pieceLength<=0 || !info.TryGetValue("name",out var name) || !Segment(name)) throw Invalid();
        if (info.TryGetValue("name.utf-8",out var utfName) && !Segment(utfName)) throw Invalid();
        long total=0;
        if (info.TryGetValue("files",out var files))
        {
            if (info.ContainsKey("length") || files is not List<object> entries || entries.Count==0) throw Invalid();
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in entries)
            {
                if (entry is not Dictionary<string,object> f || !f.TryGetValue("length",out var len) || len is not long length || length<0 || !f.TryGetValue("path",out var path) || path is not List<object> segments || segments.Count==0 || segments.Any(s=>!Segment(s))) throw Invalid();
                if (!paths.Add(string.Join('/',segments.Cast<byte[]>().Select(Encoding.Latin1.GetString))) || length>long.MaxValue-total) throw Invalid();
                if (f.TryGetValue("path.utf-8",out var utfPath) && (utfPath is not List<object> utfSegments || utfSegments.Count==0 || utfSegments.Any(s=>!Segment(s)))) throw Invalid();
                total+=length;
            }
        }
        else if (info.TryGetValue("length",out var len) && len is long length && length>0) total=length;
        else throw Invalid();
        if (total<=0 || (total-1)/pieceLength+1!=pieces.Length/20) throw Invalid();
        return (Hash(data),total);
    }
    public static string MagnetHash(string magnet)
    {
        var match = Regex.Match(Uri.UnescapeDataString(magnet), @"(?:[?&])xt=urn:btih:([a-zA-Z0-9]+)(?:&|$)", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("Only v1 or hybrid BitTorrent magnets are supported.");
        var value = match.Groups[1].Value;
        if (Regex.IsMatch(value, "^[a-fA-F0-9]{40}$")) return value.ToLowerInvariant();
        if (Regex.IsMatch(value, "^[A-Z2-7]{32}$", RegexOptions.IgnoreCase))
        {
            var output = new List<byte>(); int bits = 0, buffer = 0;
            foreach (var c in value.ToUpperInvariant()) { buffer = (buffer << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c); bits += 5; if(bits >= 8) { bits -= 8; output.Add((byte)(buffer >> bits)); } }
            return Convert.ToHexStringLower(output.ToArray());
        }
        throw new InvalidOperationException("Invalid BitTorrent info hash.");
    }
    public static string Hash(byte[] data)
    {
        int cursor = 0;
        string Bytes()
        {
            int start = cursor;
            while(cursor < data.Length && data[cursor] is >= (byte)'0' and <= (byte)'9') cursor++;
            if(cursor == start || cursor >= data.Length || data[cursor] != ':' || !int.TryParse(Encoding.ASCII.GetString(data, start, cursor-start), out var length)) throw new InvalidOperationException("Invalid torrent metadata.");
            cursor++; if(length < 0 || length > data.Length - cursor) throw new InvalidOperationException("Invalid torrent metadata.");
            var result = Encoding.UTF8.GetString(data, cursor, length); cursor += length; return result;
        }
        void Skip(int depth)
        {
            if(depth > 64 || cursor >= data.Length) throw new InvalidOperationException("Invalid torrent metadata.");
            var c = data[cursor];
            if(c == 'd' || c == 'l') { cursor++; while(cursor < data.Length && data[cursor] != 'e') Skip(depth+1); if(cursor >= data.Length) throw new InvalidOperationException("Invalid torrent metadata."); cursor++; }
            else if(c == 'i') { cursor++; var start = cursor; while(cursor < data.Length && data[cursor] != 'e') cursor++; if(cursor >= data.Length || !long.TryParse(Encoding.ASCII.GetString(data,start,cursor-start), out _)) throw new InvalidOperationException("Invalid torrent integer."); cursor++; }
            else Bytes();
        }
        if(data.Length == 0 || data[cursor++] != 'd') throw new InvalidOperationException("Download is not torrent metadata.");
        while(cursor < data.Length && data[cursor] != 'e')
        {
            var key = Bytes(); var start = cursor; Skip(0);
            if(key == "info")
            {
                var info = data.AsSpan(start, cursor-start);
                if(!Encoding.Latin1.GetString(info).Contains("6:pieces")) throw new InvalidOperationException("Pure v2 torrents are not supported yet.");
                return Convert.ToHexStringLower(SHA1.HashData(info));
            }
        }
        throw new InvalidOperationException("Torrent has no info dictionary.");
    }
}
