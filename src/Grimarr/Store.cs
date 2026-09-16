using Microsoft.Data.Sqlite;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Grimarr;

public sealed class Store
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;
    private readonly IDataProtector protector;
    public Store(IConfiguration configuration, IDataProtectionProvider protection)
    {
        protector = protection.CreateProtector("Grimarr.ConnectionSecrets.v1");
        var root = configuration["GRIMARR_CONFIG"] ?? Path.Combine(AppContext.BaseDirectory, "config");
        Directory.CreateDirectory(root);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "grimarr.db") }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS books (id TEXT PRIMARY KEY, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS activity (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, bookId TEXT NOT NULL, title TEXT NOT NULL, message TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public Settings Settings()
    {
        var settings = Read<Settings>("SELECT json FROM settings WHERE id=1").FirstOrDefault() ?? new();
        return settings with { Prowlarr = Decode(settings.Prowlarr), Qbittorrent = Decode(settings.Qbittorrent), Audiobookshelf = Decode(settings.Audiobookshelf) };
    }
    private Connection Encode(Connection connection) => connection with { ApiKey = Protect(connection.ApiKey), Password = Protect(connection.Password) };
    private Connection Decode(Connection connection) => connection with { ApiKey = Unprotect(connection.ApiKey), Password = Unprotect(connection.Password) };
    private string Protect(string value) => value.Length == 0 ? "" : "protected:" + protector.Protect(value);
    private string Unprotect(string value) => value.StartsWith("protected:",StringComparison.Ordinal) ? protector.Unprotect(value[10..]) : value;
    public List<Book> Books() => Read<Book>("SELECT json FROM books").Select(b => b with { SelectedRelease = b.SelectedRelease == null ? null : b.SelectedRelease with { DownloadUrl = b.SelectedRelease.DownloadUrl == null ? null : Unprotect(b.SelectedRelease.DownloadUrl), MagnetUrl = b.SelectedRelease.MagnetUrl == null ? null : Unprotect(b.SelectedRelease.MagnetUrl) } }).OrderByDescending(b => b.AddedAt).ToList();
    private List<T> Read<T>(string sql)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        using var rows = cmd.ExecuteReader(); var result = new List<T>();
        while (rows.Read()) result.Add(JsonSerializer.Deserialize<T>(rows.GetString(0), Json)!);
        return result;
    }
    public void Save(Settings settings) => Write("INSERT INTO settings VALUES (1,$json) ON CONFLICT(id) DO UPDATE SET json=$json", ("$json", JsonSerializer.Serialize(settings with { Prowlarr = Encode(settings.Prowlarr), Qbittorrent = Encode(settings.Qbittorrent), Audiobookshelf = Encode(settings.Audiobookshelf) }, Json)));
    public void Save(Book book) => Write("INSERT INTO books VALUES ($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json", ("$id", book.Id), ("$json", JsonSerializer.Serialize(book with { SelectedRelease = book.SelectedRelease == null ? null : book.SelectedRelease with { DownloadUrl = book.SelectedRelease.DownloadUrl == null ? null : Protect(book.SelectedRelease.DownloadUrl), MagnetUrl = book.SelectedRelease.MagnetUrl == null ? null : Protect(book.SelectedRelease.MagnetUrl) } }, Json)));
    public void Log(Book book, string message) => Write("INSERT INTO activity(at,bookId,title,message) VALUES ($at,$id,$title,$message)", ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$id", book.Id), ("$title", book.Title), ("$message", message));
    public void Remove(Book book)
    {
        if (!book.CanRemove) throw new InvalidOperationException("Only books that have not started downloading can be removed.");
        Write("DELETE FROM books WHERE id=$id", ("$id", book.Id));
        Log(book, "Removed from Grimarr");
    }
    public void SaveSelection(IReadOnlyList<Book> books)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        foreach (var book in books)
        {
            var release = book.SelectedRelease!;
            var protectedBook = book with { SelectedRelease = release with { DownloadUrl = release.DownloadUrl == null ? null : Protect(release.DownloadUrl), MagnetUrl = release.MagnetUrl == null ? null : Protect(release.MagnetUrl) } };
            using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO books VALUES ($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
            cmd.Parameters.AddWithValue("$id",book.Id); cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(protectedBook,Json)); cmd.ExecuteNonQuery();
            using var log = db.CreateCommand(); log.Transaction = transaction;
            log.CommandText = "INSERT INTO activity(at,bookId,title,message) VALUES ($at,$id,$title,'Release selected manually and queued for download')";
            log.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); log.Parameters.AddWithValue("$id",book.Id); log.Parameters.AddWithValue("$title",book.Title); log.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public List<Activity> Activities()
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,at,bookId,title,message FROM activity ORDER BY id DESC LIMIT 100";
        using var rows = cmd.ExecuteReader(); var result = new List<Activity>();
        while (rows.Read()) result.Add(new(rows.GetInt64(0), DateTimeOffset.Parse(rows.GetString(1)), rows.GetString(2), rows.GetString(3), rows.GetString(4)));
        return result;
    }
    private void Write(string sql, params (string Key, object Value)[] parameters)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key,value) in parameters) cmd.Parameters.AddWithValue(key,value);
        cmd.ExecuteNonQuery();
    }
}
