using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Grimarr;

public static class TorrentIdentity
{
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
