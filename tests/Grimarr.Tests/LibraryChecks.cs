using Grimarr;
using System.Text.Json;

static class LibraryChecks
{
    public static void Run(Action<string,bool> check,Action<string,Action> throws)
    {
        using var json=JsonDocument.Parse("""[{"id":"one","mediaType":"book","relPath":"Author/Book","media":{"numAudioFiles":1,"metadata":{"title":"L’Étoile","authors":[{"name":"Émilie"}]}}},{"id":"two","mediaType":"book","isMissing":true,"media":{"numAudioFiles":1,"metadata":{"title":"L’Étoile","authorName":"Émilie"}}},{"id":"ebook","mediaType":"book","media":{"numAudioFiles":0,"metadata":{"title":"L’Étoile","authorName":"Émilie"}}}]""");
        var items=LibraryAvailability.Parse(json.RootElement);var book=new Book {Title="L'Etoile",Author="Emilie"};var matches=LibraryAvailability.Matches(book,items);
        check("Availability matches normalize accents and punctuation",matches.Length==1 && matches[0].Id=="one" && matches[0].SameAuthor);
        check("Missing items and ebook-only records do not imply available audio",items.Count(b=>b.HasAudio)==1);
        var volumes=new[] { new LibraryBook("three","Book 3",["Author"],"","",true),new LibraryBook("four","Book 4",["Author"],"","",true) };
        check("Availability never confuses numbered volumes",LibraryAvailability.Matches(new Book {Title="Book 3",Author="Author"},volumes).Single().Id=="three");
        check("Same title by another author is an uncertain review candidate",!LibraryAvailability.Matches(book with {Author="Other"},items).Single().SameAuthor);
        check("Subtitle variants are candidates requiring review",LibraryAvailability.Matches(new Book {Title="Long Title: Edition two",Author="Author"},[new("same","Long Title: Edition one",["Author"],"","",true)]).Length==1);
        var settings=new Settings { LibraryRoot=Path.Combine(Path.GetTempPath(),"library-fixture") };
        var imported=book with { ImportedPath=Path.Combine(settings.LibraryRoot,"Author","Book [12345678]") };
        var indexed=new LibraryBook("right",book.Title,[book.Author],"Author/Book [12345678]","/different-mount/Author/Book [12345678]",true);
        check("Index confirmation uses relative directories across different mounts",LibraryAvailability.Imported(settings,imported,[indexed]).Single().Id=="right");
        check("Title-only matches never confirm a different imported directory",LibraryAvailability.Imported(settings,imported,[indexed with { RelPath="Other/Book [12345678]" }]).Length==0);
        check("Indexing requires matching reviewed author as well as directory",LibraryAvailability.Imported(settings,imported,[indexed with {Authors=["Wrong author"]}]).Length==0);
        check("Changed metadata at the actual directory remains available for explicit review",LibraryAvailability.ImportedDirectories(settings,imported,[indexed with {Title="Edited title",Authors=["Edited author"]}]).Length==1);
        check("Indexing requires readable audio",LibraryAvailability.Imported(settings,imported,[indexed with {HasAudio=false}]).Length==0);
        check("Explicit ABS path mapping confirms subfolder mounts without matching other directories",LibraryAvailability.Imported(settings with {AudiobookshelfLibraryPath="/abs/subfolder"},imported,[indexed with {Path="/abs/subfolder/Author/Book [12345678]"},indexed with {Path="/abs/other/Author/Book [12345678]"}]).Length==1);
        check("Remote library mappings reject URLs, relative paths and traversal",LibraryAvailability.ValidRemoteRoot("/audiobooks") && LibraryAvailability.ValidRemoteRoot("") && !LibraryAvailability.ValidRemoteRoot("https://example.com") && !LibraryAvailability.ValidRemoteRoot("relative") && !LibraryAvailability.ValidRemoteRoot("/audiobooks/../private"));
        throws("Index confirmation cannot escape the configured library root",()=>LibraryAvailability.Imported(settings,imported with {ImportedPath=Path.GetTempPath()},[indexed]));
        using var invalid=JsonDocument.Parse("{}");throws("Malformed library response fails closed",()=>LibraryAvailability.Parse(invalid.RootElement));
    }
}
