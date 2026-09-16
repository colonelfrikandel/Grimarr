using System.Text.RegularExpressions;

namespace Grimarr;

public static class Ranking
{
    private static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD), @"[^a-z0-9]+", " ").Trim();
    private static HashSet<string> Words(string value) => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    private static bool Has(string title, string expression) => Regex.IsMatch(title, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static RankedRelease Rank(Book book, Release release)
    {
        var reasons = new List<string>(); var eligible = true; double score = 0;
        void Reject(string reason) { eligible = false; reasons.Add(reason); }
        var title = release.Title;
        var words = Words(title);
        var expected = Words(book.Title);
        if (expected.Count == 0 || !expected.IsSubsetOf(words)) Reject("Title does not fully match");
        else { score += 100; reasons.Add("Title matches"); }
        var author = Words(book.Author);
        if (author.Count == 0 || !author.IsSubsetOf(words)) Reject("Author does not fully match");
        else score += 40;
        if (!string.Equals(release.Protocol, "torrent", StringComparison.OrdinalIgnoreCase)) Reject("Not a torrent");
        if (Has(title, @"\b(bundle|collection|omnibus|box\s*set|books\s*\d+\s*[-–]\s*\d+)\b") && !Has(book.Title, @"\b(collection|omnibus|box\s*set)\b")) Reject("Multi-book pack needs manual handling");
        if (Has(title, @"\b(epub|mobi|azw3?|pdf)\b") && !Has(title, @"\b(m4b|mp3|audiobook|flac|opus)\b")) Reject("Looks like an ebook");
        var english = release.Language?.ToLowerInvariant() is "en" or "eng" or "english" || Has(title, @"\b(english|eng)\b");
        var foreign = release.Language is { Length: > 0 } && release.Language.ToLowerInvariant() is not ("en" or "eng" or "english") || Has(title, @"\b(german|deutsch|french|francais|dutch|nederlands|spanish|espanol|russian|italian|polish)\b");
        if (foreign) Reject("Non-English or conflicting language");
        else if (!english) Reject("English language not confirmed");
        else { score += 20; reasons.Add("English confirmed"); }
        var dramatized = release.Narration == "dramatized" || Has(title, @"\b(dramati[sz]ed|full[ -]cast|graphic[ -]?audio|audio[ -]drama)\b");
        if (book.Preferences.Narration == "standard" && dramatized) Reject("Dramatized edition excluded");
        if (book.Preferences.Narration == "dramatized" && !dramatized) Reject("Dramatized edition not confirmed");
        var unabridged = release.Abridgement == "unabridged" || Has(title, @"\bunabridged\b");
        var abridged = release.Abridgement == "abridged" || Has(title, @"\babridged\b");
        if (book.Preferences.Abridgement == "unabridged" && (!unabridged || abridged)) Reject("Unabridged edition not confirmed");
        if (book.Preferences.Abridgement == "abridged" && (!abridged || unabridged)) Reject("Abridged edition not confirmed");
        if (release.Seeders < book.Preferences.MinimumSeeders) Reject("Too few seeders");
        else { score += Math.Min(15, Math.Log2(release.Seeders + 1) * 2); reasons.Add($"{release.Seeders} seeders"); }
        if (release.Size <= 0) Reject("Size not provided");
        if (book.Preferences.PreferM4b && Has(title, @"\bm4b\b")) { score += 5; reasons.Add("Preferred M4B format"); }
        if (release.ListenerRating is >= 0 and <= 5 && release.RatingCount is > 0 && !string.IsNullOrWhiteSpace(release.RatingSource))
        {
            // Shrink small samples toward 3/5 so one five-star vote cannot dominate.
            score += ((release.ListenerRating.Value * release.RatingCount.Value + 3 * 20) / (release.RatingCount.Value + 20)) * 4;
            reasons.Add($"Recording rating {release.ListenerRating:0.0}/5 ({release.RatingCount} votes; {release.RatingSource})");
        }
        else reasons.Add("Recording rating unavailable");
        return new(release, Math.Round(score, 2), eligible, reasons.ToArray());
    }
}
