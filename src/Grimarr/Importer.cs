using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Grimarr;

public sealed class Importer
{
    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int Link(string source, string target);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string target, string source, IntPtr attributes);
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4b", ".m4a", ".flac", ".ogg", ".opus" };
    public static string Under(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Path is outside the configured storage root.");
        return full;
    }
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symlinks are not supported in import paths. Mount the real storage directory.");
    }
    public static string MapPath(Settings settings, string remote)
    {
        var value = remote.Replace('\\', '/');
        foreach(var mapping in settings.PathMappings.OrderByDescending(m => m.Remote.Length))
        {
            var prefix = mapping.Remote.Replace('\\','/').TrimEnd('/');
            if(value == prefix || value.StartsWith(prefix + "/", StringComparison.Ordinal))
                return Under(settings.DownloadRoot, Path.Combine(mapping.Local, value[prefix.Length..].TrimStart('/')));
        }
        return Under(settings.DownloadRoot, value);
    }
    private static string Segment(string value)
    {
        var clean = Regex.Replace(value, "[<>:\"/\\\\|?*\\x00-\\x1F]", "_").Trim(' ', '.');
        if(string.IsNullOrWhiteSpace(clean)) clean = "Unknown";
        return clean[..Math.Min(clean.Length, 100)];
    }
    public async Task<string> Import(Settings settings, Book book, string remotePath, CancellationToken ct)
    {
        var source = MapPath(settings, remotePath); NoLinks(source); NoLinks(settings.LibraryRoot);
        if(!Directory.Exists(settings.LibraryRoot)) throw new InvalidOperationException("Library root does not exist. Check the Docker volume and Settings.");
        var final = Under(settings.LibraryRoot, Path.Combine(settings.LibraryRoot, Segment(book.Author), Segment(book.Title) + " [" + book.Id[..8] + "]"));
        NoLinks(final);
        if(Directory.Exists(final))
        {
            var manifest = Path.Combine(final, ".grimarr.json");
            if(File.Exists(manifest) && (await File.ReadAllTextAsync(manifest, ct)).Contains(book.Id, StringComparison.Ordinal)) return final;
            throw new InvalidOperationException("Destination already exists and is not owned by this import.");
        }
        if(!File.Exists(source) && !Directory.Exists(source)) throw new InvalidOperationException("Completed download is not visible to Grimarr. Check path mappings.");
        if (Directory.Exists(source) && Path.GetFullPath(final).StartsWith(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Library must not be inside the downloaded book folder.");
        // Stage alongside the library, on the same filesystem, outside the ABS library watcher.
        var parent = Directory.GetParent(Path.GetFullPath(settings.LibraryRoot))?.FullName ?? throw new InvalidOperationException("Library root cannot be the filesystem root.");
        var stage = Path.Combine(parent, ".grimarr-staging", book.Id); NoLinks(stage); Directory.CreateDirectory(stage);
        var paths = new List<string>();
        void Walk(string directory)
        {
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                NoLinks(entry);
                if(Directory.Exists(entry)) Walk(entry); else paths.Add(entry);
            }
        }
        if(File.Exists(source)) paths.Add(source); else Walk(source);
        var audio = paths.Where(p => AudioExtensions.Contains(Path.GetExtension(p))).ToList();
        if(audio.Count == 0) throw new InvalidOperationException("No supported audio files found. Archives must be unpacked manually.");
        foreach(var file in audio) await ValidateAudio(file, ct);
        var files = paths.Where(p => AudioExtensions.Contains(Path.GetExtension(p)) || Path.GetExtension(p).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png").ToList();
        foreach(var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = File.Exists(source) ? Path.GetFileName(source) : Path.GetRelativePath(source,file);
            var target = Under(stage, Path.Combine(stage,relative)); NoLinks(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if(File.Exists(target)) File.Delete(target); // Only a file inside this book's private staging folder.
            var linked = settings.ImportMode == "hardlink-or-copy" && (OperatingSystem.IsWindows() ? CreateHardLinkW(target,file,IntPtr.Zero) : Link(file,target) == 0);
            if(!linked)
            {
                await using var input = File.OpenRead(file); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output,ct);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(stage,"metadata.json"), JsonSerializer.Serialize(new { title = book.Title, authors = new[] { book.Author }, language = "en" }, Store.Json), ct);
        await File.WriteAllTextAsync(Path.Combine(stage,".grimarr.json"), JsonSerializer.Serialize(new { book.Id, book.TorrentHash, importedAt = DateTimeOffset.UtcNow }, Store.Json), ct);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!); Directory.Move(stage,final);
        return final;
    }
    private static async Task ValidateAudio(string path, CancellationToken ct)
    {
        var info = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach(var argument in new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_type:format=duration", "-of", "json", path }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("ffprobe could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); await errors;
            using var json = JsonDocument.Parse(await output);
            if(process.ExitCode != 0 || !json.RootElement.TryGetProperty("streams",out var streams) || streams.GetArrayLength() == 0) throw new InvalidOperationException("A downloaded file has no readable audio stream.");
            if(!json.RootElement.TryGetProperty("format",out var format) || !format.TryGetProperty("duration",out var duration) || !double.TryParse(duration.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds <= 0) throw new InvalidOperationException("A downloaded audio file has no valid duration.");
        }
        finally { if(!process.HasExited) process.Kill(true); }
    }
}
