using System.IO.Compression;
using System.Net;
using Convy.Services.Downloaders.Opds;
using Convy.Services.Downloads;
using Convy.Services.Extensions;
using Convy.Services.Media;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

internal static class Books
{
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

public sealed class OpdsDownloaderTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new();
    private readonly FakeSiteHandler _handler = new();
    private readonly string _downloads = Path.Combine(Path.GetTempPath(), $"convy-opds-{Guid.NewGuid():N}");
    private readonly ExtensionHost _host;
    private readonly SourceRegistry _sources;
    private readonly EfOpdsDownloadStore _store;
    private readonly OpdsDownloadQueue _queue = new();
    private readonly OpdsDownloader _downloader;
    private readonly OpdsDownloadRunner _runner;

    public OpdsDownloaderTests()
    {
        // The books come from the test catalog of a package of Convy's own API, which Convy fetches them for.
        _host = Extensions.ContentHost(_handler);
        _sources = new SourceRegistry([_host], Media.Health(), _time, NullLogger<SourceRegistry>.Instance);
        _store = new EfOpdsDownloadStore(_db);
        _downloader = new OpdsDownloader(_downloads, _store, _queue, _time, NullLogger<OpdsDownloader>.Instance);
        _runner = new OpdsDownloadRunner(_store, _sources, _queue, _time, NullLogger<OpdsDownloadRunner>.Instance);
    }

    public void Dispose()
    {
        _sources.Dispose();
        _host.Dispose();
        _db.Dispose();
        if (Directory.Exists(_downloads))
            Directory.Delete(_downloads, recursive: true);
    }

    private static OpdsPayload Book() => new("test-books", "b5", "Автор - Книга",
    [
        new OpdsFile("Автор - Книга.epub", "https://books.test/b/5/epub", null, false, 0),
        new OpdsFile("Автор - Книга.fb2", "https://books.test/b/5/fb2", 2048, true, 1),
        new OpdsFile("Автор - Книга.mobi", "https://books.test/b/5/mobi", null, false, null),
    ]);

    private string Stored(string name) => Path.Combine(_downloads, "test-books", "b5", "Автор - Книга", name);

    /// <summary>The books' files asked for (the registry also lists the other sources of the test extension).</summary>
    private List<string> Fetched => _handler.Requested.Where(r => r.StartsWith("https://books.test/b/", StringComparison.Ordinal)).ToList();

    private void File(string path, byte[] bytes, string type) => _handler.Bytes("https://books.test" + path, bytes, type);

    [Fact]
    public async Task DownloadsThePreferredFormatAndReportsTheItem()
    {
        File("/b/5/epub", "EPUB-BYTES"u8.ToArray(), "application/epub+zip");

        var itemRef = await _downloader.AddAsync(Book(), FileSelection.All, new AddOptions("convy-books"), CancellationToken.None);
        Assert.Equal("test-books/b5", itemRef);
        Assert.Equal(DownloadState.Queued, (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);

        await _runner.RunAsync(itemRef, CancellationToken.None);

        Assert.Equal("EPUB-BYTES", await System.IO.File.ReadAllTextAsync(Stored("Автор - Книга.epub")));
        var item = (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(Path.Combine(_downloads, "test-books", "b5"), item.SavePath);
        var file = Assert.Single(item.Files);
        Assert.Equal(("Автор - Книга/Автор - Книга.epub", 10L, true), (file.Path, file.Size, file.IsComplete));
        Assert.Equal("convy-books", item.Properties["Category"]);
        Assert.Equal(DownloadProviders.Opds, Assert.Single(await _downloader.GetItemsAsync(CancellationToken.None)).Provider);
        Assert.DoesNotContain(Fetched, r => r.EndsWith("/fb2"));
        // Fetched with the source's sign-in.
        Assert.Equal("secret", FakeSiteHandler.Header(Assert.Single(_handler.Requests, r => r.RequestUri!.Host == "books.test"), "X-Token"));
    }

    [Fact]
    public async Task UnzipsFb2AndFetchesOnlyTheSelectedFormats()
    {
        File("/b/5/fb2", Books.Zip("book.fb2", "<FictionBook/>"), "application/zip");

        var itemRef = await _downloader.AddAsync(Book(), new FileSelection(["Автор - Книга.fb2"]), new AddOptions(null), CancellationToken.None);
        await _runner.RunAsync(itemRef, CancellationToken.None);

        Assert.Equal("<FictionBook/>", await System.IO.File.ReadAllTextAsync(Stored("Автор - Книга.fb2")));
        Assert.False(System.IO.File.Exists(Stored("Автор - Книга.fb2.part")));
        Assert.Equal(DownloadState.Completed, (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task AWebPageInsteadOfTheFileIsRetriedThenTheNextFormatIsUsed()
    {
        File("/b/5/epub", "<html>converting…</html>"u8.ToArray(), "text/html");
        File("/b/5/fb2", Books.Zip("book.fb2", "<FictionBook/>"), "application/zip");
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
        Assert.Equal(1, Fetched.Count(r => r.EndsWith("/epub")));

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
        Assert.True(System.IO.File.Exists(Stored("Автор - Книга.fb2")));
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
        File("/b/5/epub", "EPUB"u8.ToArray(), "application/epub+zip");
        var itemRef = await _downloader.AddAsync(Book(), FileSelection.All, new AddOptions(null), CancellationToken.None);

        await _downloader.CancelAsync(itemRef, CancellationToken.None);
        await _runner.RunAsync(itemRef, CancellationToken.None);

        Assert.Empty(Fetched);
        Assert.Equal(DownloadState.Paused, (await _downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task AddingAgainKeepsFilesAlreadyOnDisk()
    {
        File("/b/5/epub", "EPUB"u8.ToArray(), "application/epub+zip");
        var itemRef = await _downloader.AddAsync(Book(), FileSelection.All, new AddOptions(null), CancellationToken.None);
        await _runner.RunAsync(itemRef, CancellationToken.None);

        await _downloader.AddAsync(Book(), FileSelection.All, new AddOptions(null), CancellationToken.None);

        Assert.Equal(OpdsDownloadState.Completed, (await _store.GetAsync(itemRef, CancellationToken.None))!.State);
        Assert.Single(Fetched);
    }

    [Fact]
    public async Task ASourceThatIsNotInstalledFailsTheDownload()
    {
        var itemRef = await _downloader.AddAsync(Book() with { CatalogId = "gone" }, FileSelection.All, new AddOptions(null), CancellationToken.None);

        await _runner.RunAsync(itemRef, CancellationToken.None);

        var download = (await _store.GetAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(OpdsDownloadState.Failed, download.State);
        Assert.Contains("'gone' is not installed", download.Error);
        Assert.Empty(Fetched);
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
