using Convy.Services.Media;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class FileTreeTests
{
    private static readonly ListedFile[] Show =
    [
        .. Enumerable.Range(1, 10).Select(i => new ListedFile($"Season 01/e{i:00}.mkv", 100)),
        .. Enumerable.Range(1, 3).Select(i => new ListedFile($"Season 02/e{i:00}.mkv", 200)),
        new("Extras/Making Of/part1.mkv", 50),
        new("Extras/en.srt", 1),
        new("readme.nfo", 2),
    ];

    [Fact]
    public void TopLevelSummarisesDirectoriesAndListsRootFiles()
    {
        var response = FileTree.Render("r_1", Show, null, null, 0, 100);

        Assert.Equal("ok", response.Status);
        Assert.Equal(["Extras/", "Season 01/", "Season 02/", "readme.nfo"], response.Entries!.Select(e => e.Path));

        var season1 = response.Entries![1];
        Assert.Equal(10, season1.Files);
        Assert.Equal(1000, season1.SizeBytes);
        Assert.Equal(10, season1.Types!["mkv"]);

        var extras = response.Entries[0];
        Assert.Equal(new Dictionary<string, int> { ["mkv"] = 1, ["srt"] = 1 }, extras.Types);
        Assert.Null(response.Entries[3].Files);
    }

    [Fact]
    public void PathExpandsOneLevel()
    {
        var response = FileTree.Render("r_1", Show, "Extras", null, 0, 100);

        Assert.Equal("Extras/", response.Path);
        Assert.Equal(["Extras/Making Of/", "Extras/en.srt"], response.Entries!.Select(e => e.Path));
    }

    [Fact]
    public void GlobReturnsAFlatList()
    {
        var response = FileTree.Render("r_1", Show, null, "**/*.srt", 0, 100);
        Assert.Equal(["Extras/en.srt"], response.Entries!.Select(e => e.Path));

        var season = FileTree.Render("r_1", Show, null, "Season 02/**", 0, 100);
        Assert.Equal(3, season.Total);
    }

    [Fact]
    public void PagesWithOffset()
    {
        var page = FileTree.Render("r_1", Show, "Season 01", null, 4, 3);

        Assert.Equal(["Season 01/e05.mkv", "Season 01/e06.mkv", "Season 01/e07.mkv"], page.Entries!.Select(e => e.Path));
        Assert.Equal(3, page.Shown);
        Assert.Equal(10, page.Total);
        Assert.True(page.HasMore);
        Assert.Contains("offset 7", page.Note);
    }

    [Fact]
    public void UnknownDirectoryIsAnError() =>
        Assert.Throws<ConvyRequestException>(() => FileTree.Render("r_1", Show, "Season 09", null, 0, 100));
}

public class FileSelectorTests
{
    private static readonly ListedFile[] Files =
    [
        new("Season 01/e1.mkv", 10),
        new("Season 02/e1.mkv", 20),
        new("Season 02/e1-sample.mkv", 1),
        new("Season 02/e2.mkv", 20),
    ];

    [Fact]
    public void IncludeThenExclude()
    {
        var selected = FileSelector.Select(Files, ["Season 02/**"], ["**/*sample*"]);
        Assert.Equal(["Season 02/e1.mkv", "Season 02/e2.mkv"], selected.Select(f => f.Path));
    }

    [Fact]
    public void ExactPathsAreAccepted()
    {
        var selected = FileSelector.Select(Files, ["Season 01/e1.mkv", "season 02/E2.mkv"], null);
        Assert.Equal(["Season 01/e1.mkv", "Season 02/e2.mkv"], selected.Select(f => f.Path));
    }

    [Fact]
    public void ExcludeAloneStartsFromEverything()
    {
        Assert.Equal(3, FileSelector.Select(Files, null, ["**/*sample*"]).Count);
    }

    [Theory]
    [InlineData("Season 03/**", null, "include pattern 'Season 03/**'")]
    [InlineData(null, "**/*.iso", "exclude pattern '**/*.iso'")]
    public void PatternMatchingNothingIsAnError(string? include, string? exclude, string message)
    {
        var ex = Assert.Throws<ConvyRequestException>(() => FileSelector.Select(
            Files, include is null ? null : [include], exclude is null ? null : [exclude]));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void EmptySelectionIsAnError() =>
        Assert.Throws<ConvyRequestException>(() => FileSelector.Select(Files, ["Season 01/**"], ["**/*.mkv"]));
}

public sealed class FileListingServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new();
    private readonly FakeSource _source = new("prowlarr:1");
    private readonly EfSearchCache _cache;
    private readonly FileListingService _service;

    public FileListingServiceTests()
    {
        _cache = new EfSearchCache(_db, _time);
        _service = new FileListingService(
            _cache,
            new SourceRegistry([new StaticProvider(_source)], Media.Health(), _time, NullLogger<SourceRegistry>.Instance),
            new StaticOptions<FilesOptions>(new FilesOptions { MetadataTimeoutSeconds = 1, MaxEntries = 100 }),
            NullLogger<FileListingService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<string> CacheResult()
    {
        var result = new CachedResult
        {
            Id = "r_1",
            SearchId = "s_1",
            Protocol = Protocol.Torrent,
            Title = "Show",
            Availability = new Availability(),
            Sources = ["prowlarr:1"],
            MatchedQueries = ["show"],
            DedupKey = "btih:aa",
            SourceId = "prowlarr:1",
            ContentId = "c",
            ExpiresAt = _time.Now + TimeSpan.FromHours(6),
        };
        await _cache.SaveResultsAsync([result], CancellationToken.None);
        return result.Id;
    }

    [Fact]
    public async Task ListsAndCachesTheFileList()
    {
        _source.Listing = new FileListing([new("a.mkv", 1)], [1, 2, 3]);
        var id = await CacheResult();

        var first = await _service.ListAsync(id, null, null, null, CancellationToken.None);
        var second = await _service.ListAsync(id, null, null, null, CancellationToken.None);

        Assert.Equal("a.mkv", Assert.Single(first.Entries!).Path);
        Assert.Equal(first.Entries, second.Entries);
        Assert.Equal(1, _source.ListingCalls);
        Assert.Equal([1, 2, 3], (await _cache.GetListingAsync(id, CancellationToken.None))!.TorrentFile);
    }

    [Fact]
    public async Task SlowSourceReportsTimeoutWithoutCaching()
    {
        _source.HangOnListing = true;
        var id = await CacheResult();

        var response = await _service.ListAsync(id, null, null, null, CancellationToken.None);

        Assert.Equal("timeout", response.Status);
        Assert.Null(await _cache.GetListingAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownResultIsAnError() =>
        await Assert.ThrowsAsync<ConvyRequestException>(() => _service.ListAsync("r_nope", null, null, null, CancellationToken.None));
}
