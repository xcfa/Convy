using System.IO.Compression;
using System.Net;
using System.Text;
using Convy.Services.Downloaders.Opds;
using Convy.Services.Downloads;
using Convy.Sources;
using Convy.Sources.Opds;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

/// <summary>Answers requests from a table of URL → response; anything else is a 404.</summary>
internal sealed class FakeCatalogHandler : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
    public List<string> Requested { get; } = [];

    public void Feed(string pathAndQuery, string xml) =>
        Routes[pathAndQuery] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/atom+xml"),
        };

    public void File(string path, byte[] bytes, string type = "application/octet-stream") =>
        Routes[path] = () =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.PathAndQuery;
        lock (Requested) Requested.Add(Uri.UnescapeDataString(key));
        return Task.FromResult(Routes.TryGetValue(key, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal static class OpdsFixtures
{
    public const string Root = "https://books.example/opds";

    public static string Feed(string entries, string? next = null) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <feed xmlns="http://www.w3.org/2005/Atom" xmlns:dc="http://purl.org/dc/terms/">
           <title>Test catalog</title>
           <link href="/opds/search?searchTerm={"{searchTerms}"}" rel="search" type="application/atom+xml" />
           {(next is null ? "" : $"<link href=\"{next}\" rel=\"next\" type=\"application/atom+xml;profile=opds-catalog\" />")}
           {entries}
         </feed>
         """;

    public static string Book(int id, string title, string author = "Писатель Иван Петрович", string year = "1972", string size = "971 Kb") =>
        $"""
         <entry>
           <title>{title}</title>
           <author><name>{author}</name><uri>/a/1</uri></author>
           <dc:language>ru</dc:language>
           <dc:issued>{year}</dc:issued>
           <content type="text/html">Описание.&lt;br/&gt;Формат: fb2&lt;br/&gt;Размер: {size}&lt;br/&gt;</content>
           <link href="/b/{id}/fb2" rel="http://opds-spec.org/acquisition/open-access" type="application/fb2+zip" />
           <link href="/b/{id}/epub" rel="http://opds-spec.org/acquisition/open-access" type="application/epub+zip" />
           <link href="/b/{id}/mobi" rel="http://opds-spec.org/acquisition/open-access" type="application/x-mobipocket-ebook" />
           <link href="/b/{id}" rel="alternate" type="text/html" title="Книга на сайте" />
         </entry>
         """;

    public static string Author(int id, string name, int books) =>
        $"""
         <entry>
           <title>{name}</title>
           <content type="text">{books} книг</content>
           <link href="/opds/author/{id}" type="application/atom+xml;profile=opds-catalog" />
         </entry>
         """;

    public static OpdsCatalog Catalog(bool authors = true) => new()
    {
        Id = "test",
        Name = "Test catalog",
        Url = new Uri(Root),
        SearchUrl = "/opds/search?searchType=books&searchTerm={searchTerms}",
        AuthorSearchUrl = authors ? "/opds/search?searchType=authors&searchTerm={searchTerms}" : null,
        FlibustaAuthorPages = true,
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static byte[] Zip(string name, string text)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }

        return buffer.ToArray();
    }
}

public class OpdsFeedTests
{
    [Fact]
    public void ParsesBooksAuthorsLinksAndPaging()
    {
        var xml = OpdsFixtures.Feed(OpdsFixtures.Book(93857, "Пикник на обочине") + OpdsFixtures.Author(19189, "Стругацкий Аркадий", 345), next: "/opds/page/1");

        var feed = OpdsFeedParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), new Uri(OpdsFixtures.Root));

        Assert.Equal(new Uri("https://books.example/opds/page/1"), feed.Next);
        var book = feed.Entries[0];
        Assert.True(book.IsBook);
        Assert.Equal(("Пикник на обочине", "ru", "1972"), (book.Title, book.Language, book.Issued));
        Assert.Equal(["Писатель Иван Петрович"], book.Authors);
        Assert.Equal("https://books.example/b/93857/epub", book.Links.Single(l => l.Type == "application/epub+zip").Href.ToString());
        Assert.Equal(971 * 1024, OpdsContent.Size(book.Content));

        var author = feed.Entries[1];
        Assert.False(author.IsBook);
        Assert.True(Assert.Single(author.Links).IsNavigation);
        Assert.Equal(345, OpdsContent.BookCount(author.Content));
    }

    [Fact]
    public void RejectsWhatIsNotAFeed()
    {
        Assert.Throws<SourceException>(() =>
            OpdsFeedParser.Parse(new MemoryStream("<html><body>blocked</body></html>"u8.ToArray()), new Uri(OpdsFixtures.Root)));
        Assert.Throws<SourceException>(() =>
            OpdsFeedParser.Parse(new MemoryStream("not xml"u8.ToArray()), new Uri(OpdsFixtures.Root)));
    }

    [Theory]
    [InlineData("application/epub+zip", "/b/1/epub", "epub", "epub", false)]
    [InlineData("application/fb2+zip", "/b/1/fb2", "fb2", "fb2.zip", true)]
    [InlineData("application/x-mobipocket-ebook", "/b/1/mobi", "mobi", "mobi", false)]
    [InlineData("application/txt+zip", "/b/1/txt", "txt", "txt.zip", false)]
    [InlineData(null, "/files/book.djvu", "djvu", "djvu", false)]
    [InlineData("application/x-unknown", "/b/1/download", "unknown", "unknown", false)]
    public void RecognisesFormats(string? type, string path, string format, string extension, bool zipped)
    {
        Assert.Equal(new OpdsFormat(format, extension, zipped), OpdsBooks.FormatOf(type, "https://books.example" + path));
    }

    [Fact]
    public void BooksGetStableKeysAndSafeNames()
    {
        var feed = OpdsFeedParser.Parse(
            new MemoryStream(Encoding.UTF8.GetBytes(OpdsFixtures.Feed(OpdsFixtures.Book(93857, "Книга: \"Первая\"/часть?")))),
            new Uri(OpdsFixtures.Root));

        var book = OpdsBooks.ToBook("test", feed.Entries[0])!;

        Assert.Equal("b93857", book.BookKey);
        Assert.Equal("Писатель Иван Петрович - Книга Первая часть", OpdsBooks.FolderOf(book.Title, book.Authors));

        var files = OpdsBooks.Files(book, ["epub", "fb2"], unzipFb2: true);
        Assert.Equal(
            ["Писатель Иван Петрович - Книга Первая часть.epub", "Писатель Иван Петрович - Книга Первая часть.fb2", "Писатель Иван Петрович - Книга Первая часть.mobi"],
            files.Select(f => f.Name));
        Assert.Equal([0, 1, (int?)null], files.Select(f => f.Preference));
        Assert.Equal([false, true, false], files.Select(f => f.Unzip));
        Assert.Equal([null, 971L * 1024, null], files.Select(f => f.Size));

        Assert.EndsWith(".fb2.zip", OpdsBooks.Files(book, ["fb2"], unzipFb2: false)[0].Name);
        Assert.Equal("Писатель Иван Петрович — Книга: \"Первая\"/часть? (1972, ru) [epub/fb2/mobi]", OpdsBooks.DisplayTitle(book, ["epub", "fb2"]));
    }
}

public class OpdsOptionsTests
{
    [Fact]
    public void FlibustaPresetFillsSearchAndAuthorAddresses()
    {
        var catalog = new OpdsCatalogOptions { Id = "flibusta", Type = "flibusta" }.ToCatalog(out var problem)!;

        Assert.Null(problem);
        Assert.Equal(("Flibusta", "https://flibusta.site/opds"), (catalog.Name, catalog.Url.ToString()));
        Assert.Contains("searchType=books", catalog.SearchUrl);
        Assert.Contains("searchType=authors", catalog.AuthorSearchUrl);
        Assert.True(catalog.FlibustaAuthorPages);
        Assert.Equal(["epub", "fb2"], catalog.Formats);
        Assert.Equal(TimeSpan.FromSeconds(120), catalog.Timeout);
    }

    [Fact]
    public void AGenericCatalogDiscoversItsSearchAndNormalisesFormats()
    {
        var catalog = new OpdsCatalogOptions { Id = "gutenberg", Url = "https://gutenberg.example/opds", Formats = [".EPUB", "pdf", "epub"] }
            .ToCatalog(out _)!;

        Assert.Null(catalog.SearchUrl);
        Assert.Null(catalog.AuthorSearchUrl);
        Assert.False(catalog.FlibustaAuthorPages);
        Assert.Equal(["epub", "pdf"], catalog.Formats);
    }

    [Theory]
    [InlineData("my catalog", "https://x/opds", null, "id must be")]
    [InlineData("ok", "ftp://x/opds", null, "url must be")]
    [InlineData("ok", "https://x/opds", "not a proxy", "proxy")]
    public void RejectsBadSettings(string id, string url, string? proxy, string message)
    {
        Assert.Null(new OpdsCatalogOptions { Id = id, Url = url, Proxy = proxy }.ToCatalog(out var problem));
        Assert.Contains(message, problem);
    }

    [Fact]
    public void DuplicateIdsAreReported()
    {
        var options = new OpdsOptions
        {
            Catalogs = [new() { Id = "a", Url = "https://a/opds" }, new() { Id = "a", Url = "https://b/opds" }, new() { Id = "", Url = "https://c" }],
        };

        Assert.Single(options.Resolve(out var problems));
        Assert.Equal(2, problems.Count);
    }
}

public sealed class OpdsSourceTests : IDisposable
{
    private readonly FakeCatalogHandler _handler = new();
    private readonly OpdsClient _client;
    private readonly OpdsSource _source;

    public OpdsSourceTests()
    {
        _client = new OpdsClient(OpdsFixtures.Catalog(), new HttpClient(_handler));
        _source = new OpdsSource(_client, SourceStatus.Ok, null, NullLogger<OpdsSource>.Instance);
    }

    public void Dispose() => _client.Dispose();

    private static SearchRequest Request(string query) => new(query, new CategorySearchSettings("books", [], []));

    private async Task<List<ContentInfo>> Search(string query)
    {
        var results = new List<ContentInfo>();
        await foreach (var content in _source.SearchAsync(Request(query), CancellationToken.None))
            results.Add(content);
        return results;
    }

    [Fact]
    public async Task FindsBooksByTitleAndTheBooksOfMatchingAuthors()
    {
        _handler.Feed("/opds/search?searchType=books&searchTerm=%D0%BF%D0%B8%D0%BA%D0%BD%D0%B8%D0%BA",
            OpdsFixtures.Feed(OpdsFixtures.Book(1, "Пикник на обочине")));
        _handler.Feed("/opds/search?searchType=authors&searchTerm=%D0%BF%D0%B8%D0%BA%D0%BD%D0%B8%D0%BA",
            OpdsFixtures.Feed(OpdsFixtures.Author(10, "Однофамилец", 1) + OpdsFixtures.Author(20, "Пикников Главный", 30)
                              + OpdsFixtures.Author(30, "Третий", 0)));
        _handler.Feed("/opds/author/20/alphabet", OpdsFixtures.Feed(OpdsFixtures.Book(1, "Пикник на обочине") + OpdsFixtures.Book(2, "Вторая")));
        _handler.Feed("/opds/author/20/alphabet/1", OpdsFixtures.Feed(OpdsFixtures.Book(3, "Третья")));
        _handler.Feed("/opds/author/10/alphabet", OpdsFixtures.Feed(OpdsFixtures.Book(4, "Чужая")));

        var results = await Search("пикник");

        // Book 1 is found by title and on the author's page: once. The author with 0 books is
        // beyond MaxAuthors (2); the one with 30 books has two pages.
        Assert.Equal(["test:b1", "test:b2", "test:b3", "test:b4"], results.Select(r => r.DedupKey!.Replace("opds:", "")).Order());
        Assert.DoesNotContain(_handler.Requested, r => r.Contains("/author/30/"));
        Assert.Contains("/opds/author/20/alphabet/1", _handler.Requested);

        var first = results.First(r => r.DedupKey == "opds:test:b1");
        Assert.Equal("Писатель Иван Петрович — Пикник на обочине (1972, ru) [epub/fb2/mobi]", first.Title);
        Assert.Equal(971 * 1024, first.SizeBytes);
        Assert.Equal(1, first.FileCount);
        Assert.Equal(TimeSpan.FromSeconds(30), _source.Timeout);
    }

    [Fact]
    public async Task AFailingPartOfTheSearchLeavesTheOther()
    {
        _handler.Feed("/opds/search?searchType=authors&searchTerm=x", OpdsFixtures.Feed(OpdsFixtures.Author(20, "Автор", 5)));
        _handler.Feed("/opds/author/20/alphabet", OpdsFixtures.Feed(OpdsFixtures.Book(7, "Книга")));

        var results = await Search("x");

        Assert.Equal("opds:test:b7", Assert.Single(results).DedupKey);
    }

    [Fact]
    public async Task FailsWhenEverySearchFails()
    {
        var ex = await Assert.ThrowsAsync<SourceException>(() => Search("nothing"));
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task ListsFormatsAsFilesAndResolvesThePayload()
    {
        _handler.Feed("/opds/search?searchType=books&searchTerm=x", OpdsFixtures.Feed(OpdsFixtures.Book(5, "Книга")));
        _handler.Feed("/opds/search?searchType=authors&searchTerm=x", OpdsFixtures.Feed(""));
        var content = Assert.Single(await Search("x"));

        var listing = await _source.ListFilesAsync(content.ContentId, CancellationToken.None);
        var payload = Assert.IsType<OpdsPayload>(await _source.ResolveAsync(content.ContentId, CancellationToken.None));

        Assert.Equal(["Писатель Иван Петрович - Книга.epub", "Писатель Иван Петрович - Книга.fb2", "Писатель Иван Петрович - Книга.mobi"],
            listing.Files.Select(f => f.Path));
        Assert.Equal(("test", "b5", "Писатель Иван Петрович - Книга"), (payload.CatalogId, payload.BookKey, payload.Folder));
        Assert.Equal("https://books.example/b/5/epub", payload.Files[0].Url);
    }
}

public sealed class OpdsDownloaderTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new();
    private readonly FakeCatalogHandler _handler = new();
    private readonly string _downloads = Path.Combine(Path.GetTempPath(), $"convy-opds-{Guid.NewGuid():N}");
    private readonly OpdsCatalogs _catalogs;
    private readonly EfOpdsDownloadStore _store;
    private readonly OpdsDownloadQueue _queue = new();
    private readonly OpdsDownloader _downloader;
    private readonly OpdsDownloadRunner _runner;

    public OpdsDownloaderTests()
    {
        _catalogs = new OpdsCatalogs([new OpdsClient(OpdsFixtures.Catalog(), new HttpClient(_handler))]);
        _store = new EfOpdsDownloadStore(_db);
        _downloader = new OpdsDownloader(_downloads, _store, _queue, _time, NullLogger<OpdsDownloader>.Instance);
        _runner = new OpdsDownloadRunner(_store, _catalogs, _queue, _time, NullLogger<OpdsDownloadRunner>.Instance);
    }

    public void Dispose()
    {
        _catalogs.Dispose();
        _db.Dispose();
        if (Directory.Exists(_downloads))
            Directory.Delete(_downloads, recursive: true);
    }

    private static OpdsPayload Book() => new("test", "b5", "Автор - Книга",
    [
        new OpdsFile("Автор - Книга.epub", "https://books.example/b/5/epub", null, false, 0),
        new OpdsFile("Автор - Книга.fb2", "https://books.example/b/5/fb2", 2048, true, 1),
        new OpdsFile("Автор - Книга.mobi", "https://books.example/b/5/mobi", null, false, null),
    ]);

    private string Stored(string name) => Path.Combine(_downloads, "test", "b5", "Автор - Книга", name);

    [Fact]
    public async Task DownloadsThePreferredFormatAndReportsTheItem()
    {
        _handler.File("/b/5/epub", "EPUB-BYTES"u8.ToArray(), "application/epub+zip");

        var itemRef = await _downloader.AddAsync(Book(), FileSelection.All, new AddOptions("convy-books"), CancellationToken.None);
        Assert.Equal("test/b5", itemRef);
        Assert.Equal(DownloadState.Queued, (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);

        await _runner.RunAsync(itemRef, CancellationToken.None);

        Assert.Equal("EPUB-BYTES", await File.ReadAllTextAsync(Stored("Автор - Книга.epub")));
        var item = (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(Path.Combine(_downloads, "test", "b5"), item.SavePath);
        var file = Assert.Single(item.Files);
        Assert.Equal(("Автор - Книга/Автор - Книга.epub", 10L, true), (file.Path, file.Size, file.IsComplete));
        Assert.Equal("convy-books", item.Properties["Category"]);
        Assert.Equal(DownloadProviders.Opds, Assert.Single(await _downloader.GetItemsAsync(CancellationToken.None)).Provider);
        Assert.DoesNotContain(_handler.Requested, r => r.EndsWith("/fb2"));
    }

    [Fact]
    public async Task UnzipsFb2AndFetchesOnlyTheSelectedFormats()
    {
        _handler.File("/b/5/fb2", OpdsFixtures.Zip("book.fb2", "<FictionBook/>"), "application/zip");

        var itemRef = await _downloader.AddAsync(Book(), new FileSelection(["Автор - Книга.fb2"]), new AddOptions(null), CancellationToken.None);
        await _runner.RunAsync(itemRef, CancellationToken.None);

        Assert.Equal("<FictionBook/>", await File.ReadAllTextAsync(Stored("Автор - Книга.fb2")));
        Assert.False(File.Exists(Stored("Автор - Книга.fb2.part")));
        Assert.Equal(DownloadState.Completed, (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task AWebPageInsteadOfTheFileIsRetriedThenTheNextFormatIsUsed()
    {
        _handler.File("/b/5/epub", "<html>converting…</html>"u8.ToArray(), "text/html");
        _handler.File("/b/5/fb2", OpdsFixtures.Zip("book.fb2", "<FictionBook/>"), "application/zip");
        var itemRef = await _downloader.AddAsync(Book(), FileSelection.All, new AddOptions(null), CancellationToken.None);

        await _runner.RunAsync(itemRef, CancellationToken.None);

        var download = (await _store.GetAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(OpdsDownloadState.Retrying, download.State);
        Assert.Contains("web page", download.Error);
        Assert.Equal(_time.Now.AddMinutes(1), download.NextAttemptAt);
        var item = (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(DownloadState.Errored, item.State);

        // Only a queued download runs: asking again before the retry time does nothing.
        await _runner.RunAsync(itemRef, CancellationToken.None);
        Assert.Equal(1, _handler.Requested.Count(r => r.EndsWith("/epub")));

        for (var attempt = 2; attempt <= OpdsDownloadRunner.MaxAttempts; attempt++)
        {
            await _store.TryUpdateAsync(download with { State = OpdsDownloadState.Queued }, OpdsDownloadState.Retrying, CancellationToken.None);
            await _runner.RunAsync(itemRef, CancellationToken.None);
            download = (await _store.GetAsync(itemRef, CancellationToken.None))!;
        }

        // After the last attempt the FB2 takes the EPUB's place.
        Assert.Equal(OpdsDownloadState.Queued, download.State);
        Assert.Contains("Trying Автор - Книга.fb2 instead", download.Error);
        Assert.Equal(["Автор - Книга.fb2"], download.WantedFiles.Select(f => f.Name));

        await _runner.RunAsync(itemRef, CancellationToken.None);
        Assert.Equal(OpdsDownloadState.Completed, (await _store.GetAsync(itemRef, CancellationToken.None))!.State);
        Assert.True(File.Exists(Stored("Автор - Книга.fb2")));
    }

    [Fact]
    public async Task AnExplicitlySelectedFormatHasNoFallback()
    {
        var itemRef = await _downloader.AddAsync(Book(), new FileSelection(["Автор - Книга.mobi"]), new AddOptions(null), CancellationToken.None);

        for (var attempt = 1; attempt <= OpdsDownloadRunner.MaxAttempts; attempt++)
        {
            var current = (await _store.GetAsync(itemRef, CancellationToken.None))!;
            if (current.State == OpdsDownloadState.Retrying)
                await _store.TryUpdateAsync(current with { State = OpdsDownloadState.Queued }, OpdsDownloadState.Retrying, CancellationToken.None);
            await _runner.RunAsync(itemRef, CancellationToken.None);
        }

        var download = (await _store.GetAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(OpdsDownloadState.Failed, download.State);
        Assert.Contains("404", download.Error);
        Assert.Equal(DownloadState.Failed, (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task ACancelledDownloadIsNotStarted()
    {
        _handler.File("/b/5/epub", "EPUB"u8.ToArray(), "application/epub+zip");
        var itemRef = await _downloader.AddAsync(Book(), FileSelection.All, new AddOptions(null), CancellationToken.None);

        await _downloader.CancelAsync(itemRef, CancellationToken.None);
        await _runner.RunAsync(itemRef, CancellationToken.None);

        Assert.Empty(_handler.Requested);
        Assert.Equal(DownloadState.Paused, (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task AddingAgainKeepsFilesAlreadyOnDisk()
    {
        _handler.File("/b/5/epub", "EPUB"u8.ToArray(), "application/epub+zip");
        var itemRef = await _downloader.AddAsync(Book(), FileSelection.All, new AddOptions(null), CancellationToken.None);
        await _runner.RunAsync(itemRef, CancellationToken.None);

        await _downloader.AddAsync(Book(), FileSelection.All, new AddOptions(null), CancellationToken.None);

        Assert.Equal(OpdsDownloadState.Completed, (await _store.GetAsync(itemRef, CancellationToken.None))!.State);
        Assert.Single(_handler.Requested);
    }

    [Fact]
    public async Task RejectsASelectionWithoutAnyFormatOfTheBook()
    {
        await Assert.ThrowsAsync<ConvyRequestException>(() =>
            _downloader.AddAsync(Book(), new FileSelection(["other.pdf"]), new AddOptions(null), CancellationToken.None));
    }
}

public class SourceExceptionTests
{
    [Fact]
    public void UnreachableSpellsOutWhatTheInnerExceptionsSay()
    {
        var tls = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new IOException("Received an unexpected EOF or 0 bytes from the transport stream."));

        Assert.Equal(
            "Flibusta is unreachable: The SSL connection could not be established: Received an unexpected EOF or 0 bytes from the transport stream.",
            SourceException.Unreachable("Flibusta", tls).Message);
    }

    [Fact]
    public void UnreachableDoesNotRepeatACauseTheMessageAlreadyHas()
    {
        var refused = new HttpRequestException(
            "Connection refused (prowlarr:9696)", new System.Net.Sockets.SocketException(111));

        var message = SourceException.Unreachable("Prowlarr", refused).Message;

        Assert.StartsWith("Prowlarr is unreachable: Connection refused (prowlarr:9696)", message);
        Assert.Equal(SourceErrorKind.Error, SourceException.Unreachable("Prowlarr", refused).Kind);
    }
}
