using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Media;
using Convy.Services.Security;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public sealed class MediaDownloadServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new();
    private readonly FakeSource _source = new("prowlarr:1");
    private readonly FakeDownloader _downloader = new();
    private readonly FakeDownloader _books = new(DownloadProviders.Opds, Protocol.Opds);
    private readonly EfSearchCache _cache;
    private readonly MediaDownloadService _service;
    private readonly JobService _jobs;

    public MediaDownloadServiceTests()
    {
        _cache = new EfSearchCache(_db, _time);
        var registry = new SourceRegistry([new StaticProvider(_source)], Media.Health(), _time, NullLogger<SourceRegistry>.Instance);
        var catalog = Media.Catalog(Media.Categories());
        var store = new EfJobStore(_db);
        var rules = new FakeRules
        {
            Yaml =
                """
                rules:
                  - name: music
                    condition: "Category == Music"
                    path: /data/media/music
                """,
        };
        _jobs = new JobService(
            new DownloaderResolver([_downloader, _books]), rules, store, new JobTransitions(store, new RecordingJobEvents()),
            new FakeFileSystem(), new StaticOptions<JobOptions>(new JobOptions()), _time, NullLogger<JobService>.Instance);

        _downloader.OnAdd = p => FakeDownloader.Item(((TorrentPayload)p).InfoHash, DownloadState.Queued);

        _service = new MediaDownloadService(
            catalog,
            registry,
            _cache,
            new FileListingService(_cache, registry,
                new StaticOptions<FilesOptions>(new FilesOptions { MetadataTimeoutSeconds = 1 }), NullLogger<FileListingService>.Instance),
            _jobs,
            new StaticOptions<SearchOptions>(new SearchOptions()));
    }

    public void Dispose() => _db.Dispose();

    private async Task CacheResult()
    {
        await _cache.SaveResultsAsync([new CachedResult
        {
            Id = "r_1",
            SearchId = "s_1",
            Protocol = Protocol.Torrent,
            Title = "Evanescence - Fallen",
            SizeBytes = 999,
            FileCount = 3,
            Availability = new Availability { Seeders = 4 },
            Sources = ["prowlarr:1"],
            MatchedQueries = ["fallen"],
            DedupKey = "btih:abc",
            SourceId = "prowlarr:1",
            InfoUrl = "https://tracker.example/topic/42",
            ContentId = "c",
            ExpiresAt = _time.Now + TimeSpan.FromHours(6),
        }], CancellationToken.None);
    }

    [Fact]
    public async Task ABookCountsOnlyThePreferredFormat()
    {
        await CacheAlbums(("r_1", "Пушкин — Капитанская дочка"));
        _source.PayloadFor = _ => new OpdsPayload("flibusta", "b1", "Пушкин - Капитанская дочка",
        [
            new OpdsFile("Пушкин - Капитанская дочка.epub", "https://x/b/1/epub", null, false, 0),
            new OpdsFile("Пушкин - Капитанская дочка.fb2", "https://x/b/1/fb2", 2048, true, 1),
        ]);
        _books.OnAdd = p => FakeDownloader.Item(((OpdsPayload)p).BookKey, DownloadState.Queued);

        var response = await _service.DownloadAsync("r_1", "music", null, null, null, CancellationToken.None);

        Assert.Equal(1, response.FileCount);
        Assert.Null(response.SizeBytes);
        Assert.True(Assert.Single(_books.Added).Selection.IsAll);
    }

    private async Task CacheAlbums(params (string Id, string Title)[] albums)
    {
        await _cache.SaveResultsAsync(albums.Select(a => new CachedResult
        {
            Id = a.Id,
            SearchId = "s_1",
            Protocol = Protocol.Torrent,
            Title = a.Title,
            SizeBytes = 100,
            FileCount = 10,
            Availability = new Availability { Seeders = 4 },
            Sources = ["prowlarr:1"],
            MatchedQueries = ["evanescence"],
            DedupKey = "btih:" + a.Id,
            SourceId = "prowlarr:1",
            ContentId = a.Id,
            ExpiresAt = _time.Now + TimeSpan.FromHours(6),
        }).ToList(), CancellationToken.None);
        _source.PayloadFor = id => new TorrentPayload(id, $"magnet:?xt=urn:btih:{id}", null);
    }

    [Fact]
    public async Task SeveralReleasesBecomeOneJob()
    {
        await CacheAlbums(("r_1", "Evanescence - Fallen"), ("r_2", "Evanescence - The Open Door"));

        var response = await _service.DownloadAsync("music", null, null, null, null, [new DownloadRelease("r_1", "Evanescence/2003 - Fallen"), new DownloadRelease("r_2", "Evanescence/2006 - The Open Door")], null, CancellationToken.None);

        Assert.Equal("j_1", response.JobId);
        Assert.Equal(2, _downloader.Added.Count);
        Assert.Equal("/data/media/music/Evanescence", FakeFileSystem.Norm(response.ExpectedPath!));
        Assert.Equal(200, response.SizeBytes);
        Assert.Equal(20, response.FileCount);
        Assert.Equal(["r_1", "r_2"], response.Releases!.Select(r => r.ResultId));
        Assert.Equal("/data/media/music/Evanescence/2006 - The Open Door", FakeFileSystem.Norm(response.Releases![1].ExpectedPath!));
        Assert.Null(response.Failed);

        var job = Assert.Single(await _jobs.ListAsync(null, null, CancellationToken.None));
        Assert.Equal(2, job.Releases.Count);
    }

    [Fact]
    public async Task ResultIdAndReleasesAreExclusive()
    {
        await CacheAlbums(("r_1", "Evanescence - Fallen"));

        var both = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.DownloadAsync("music", "r_1", null, null, null, [new DownloadRelease("r_1")], null, CancellationToken.None));
        Assert.Contains("not both", both.Message);

        var none = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.DownloadAsync("music", null, null, null, null, null, null, CancellationToken.None));
        Assert.Contains("Pass result_id", none.Message);

        var unknown = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.DownloadAsync("music", null, null, null, null, [new DownloadRelease("r_1"), new DownloadRelease("r_9")], null, CancellationToken.None));
        Assert.StartsWith("r_9: ", unknown.Message);
        Assert.Empty(_downloader.Added);
    }

    private static readonly FileListing Album = new(
        [new("01 - Going Under.flac", 30), new("02 - Bring Me to Life.flac", 40), new("cover.jpg", 1)],
        [7, 7, 7]);

    [Fact]
    public async Task DownloadsEverythingWithoutPatterns()
    {
        await CacheResult();

        var response = await _service.DownloadAsync("r_1", "music", "Evanescence/2003 - Fallen", null, null, CancellationToken.None);

        var (payload, selection, options) = Assert.Single(_downloader.Added);
        Assert.True(selection.IsAll);
        Assert.Equal("Music", options.Category);
        Assert.Null(((TorrentPayload)payload).TorrentFile);
        Assert.Equal("j_1", response.JobId);
        Assert.Equal("queued", response.Status);
        Assert.Equal("music", response.Rule);
        Assert.Equal("/data/media/music/Evanescence/2003 - Fallen", FakeFileSystem.Norm(response.ExpectedPath!));
        Assert.Equal(999, response.SizeBytes);
        Assert.Equal(0, _source.ListingCalls);
        Assert.Null(response.Instruction);
    }

    [Fact]
    public async Task TheJobRemembersWhereTheReleaseCameFrom()
    {
        await CacheResult();

        await _service.DownloadAsync("r_1", "music", "Evanescence/2003 - Fallen", null, null, CancellationToken.None);

        var job = Assert.Single(await _jobs.ListAsync(null, null, CancellationToken.None));
        Assert.Equal(new JobOrigin("prowlarr:1", "https://tracker.example/topic/42", "fallen"), job.Job.Origin);
    }

    [Fact]
    public async Task AnInstructionIsKeptWithTheJobAndAnnounced()
    {
        await CacheResult();

        var response = await _service.DownloadAsync(
            "music", "r_1", "Evanescence/2003 - Fallen", null, null, null, "  Tag the album with beets.  ", CancellationToken.None);

        Assert.Equal("Tag the album with beets.", response.Instruction);
        Assert.Contains("handed off for processing", response.Note);
        Assert.Equal("/data/media/music/Evanescence/2003 - Fallen", FakeFileSystem.Norm(response.ExpectedPath!));
        var job = Assert.Single(await _jobs.ListAsync(null, null, CancellationToken.None));
        Assert.Equal("Tag the album with beets.", Assert.Single(job.Job.Releases).Instruction);
        Assert.Equal("Tag the album with beets.", job.Job.Instruction);

        // The rule's folder is the forecast destination from the start.
        Assert.Equal("/data/media/music/Evanescence/2003 - Fallen", FakeFileSystem.Norm(job.Job.Destination!));
    }

    [Fact]
    public async Task ATooLongInstructionIsRejectedBeforeDownloading()
    {
        await CacheResult();

        var error = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.DownloadAsync(
            "music", "r_1", null, null, null, null, new string('x', JobInstructions.MaxLength + 1), CancellationToken.None));

        Assert.Contains("too long", error.Message);
        Assert.Empty(_downloader.Added);
    }

    [Fact]
    public async Task PatternsSelectFilesAndHandOverTheMetadata()
    {
        await CacheResult();
        _source.Listing = Album;

        var response = await _service.DownloadAsync("r_1", "music", null, ["*.flac"], ["02*"], CancellationToken.None);

        var (payload, selection, _) = Assert.Single(_downloader.Added);
        Assert.Equal(["01 - Going Under.flac"], selection.Paths);
        Assert.Equal([7, 7, 7], ((TorrentPayload)payload).TorrentFile);
        Assert.Equal(1, response.FileCount);
        Assert.Equal(30, response.SizeBytes);
    }

    [Fact]
    public async Task PatternsSelectingEverythingDownloadEverything()
    {
        await CacheResult();
        _source.Listing = Album;

        await _service.DownloadAsync("r_1", "music", null, ["**/*"], null, CancellationToken.None);

        Assert.True(Assert.Single(_downloader.Added).Selection.IsAll);
    }

    [Fact]
    public async Task PatternsWithoutAFileListAreRejected()
    {
        await CacheResult();
        _source.HangOnListing = true;

        var ex = await Assert.ThrowsAsync<ConvyRequestException>(
            () => _service.DownloadAsync("r_1", "music", null, ["*.flac"], null, CancellationToken.None));

        Assert.Contains("without include/exclude", ex.Message);
        Assert.Empty(_downloader.Added);
    }

    [Theory]
    [InlineData("r_1", "films", null, "Unknown category")]
    [InlineData("r_1", "music", "../x", "'..'")]
    [InlineData("r_2", "music", null, "unknown or expired")]
    public async Task InvalidRequestsAreRejectedBeforeDownloading(string resultId, string category, string? subpath, string message)
    {
        await CacheResult();

        var ex = await Assert.ThrowsAsync<ConvyRequestException>(
            () => _service.DownloadAsync(resultId, category, subpath, null, null, CancellationToken.None));

        Assert.Contains(message, ex.Message);
        Assert.Empty(_downloader.Added);
    }
}

public class CategoryCatalogTests
{
    [Fact]
    public void RejectsConfigurationWithoutOtherAndKeepsThePreviousOne()
    {
        var monitor = new MutableOptions<CategoriesOptions>(new CategoriesOptions { Categories = Media.Categories() });
        var catalog = new CategoryCatalog(monitor, NullLogger<CategoryCatalog>.Instance);

        monitor.Set(new CategoriesOptions { Categories = new() { ["movies"] = new CategoryOptions() } });

        Assert.Equal(["music", "other"], catalog.All.Select(c => c.Id));
    }

    [Fact]
    public void ExposesProviderSettingsAndClientCategory()
    {
        var categories = Media.Categories("soulseek");
        categories["music"].Prowlarr = new ProwlarrCategoryOptions { Categories = [3000] };
        categories["music"].Soulseek = new SoulseekCategoryOptions { Extensions = [".FLAC", "mp3"] };

        var music = Media.Catalog(categories).Get("MUSIC");

        Assert.Equal("Music", music.ClientCategory);
        Assert.Equal([3000], music.SearchSettings.ProwlarrCategories);
        Assert.Equal(["flac", "mp3"], music.SearchSettings.SoulseekExtensions);
        Assert.Equal(["soulseek"], music.Sources);
    }

    private sealed class MutableOptions<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        private Action<T, string?>? _listener;

        public MutableOptions(T value) => CurrentValue = value;

        public T CurrentValue { get; private set; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listener = listener;
            return null;
        }

        public void Set(T value)
        {
            CurrentValue = value;
            _listener?.Invoke(value, null);
        }
    }
}

public class McpApiKeyValidatorTests
{
    private static McpApiKeyValidator Create(string? key) => new(new StaticOptions<McpOptions>(new McpOptions { ApiKey = key }));

    [Theory]
    [InlineData("k3y", null, McpAccess.Granted)]
    [InlineData(null, "Bearer k3y", McpAccess.Granted)]
    [InlineData("wrong", null, McpAccess.Denied)]
    [InlineData(null, "Basic k3y", McpAccess.Denied)]
    [InlineData(null, null, McpAccess.Denied)]
    public void ChecksTheKeyFromEitherHeader(string? apiKey, string? authorization, McpAccess expected) =>
        Assert.Equal(expected, Create("k3y").Check(apiKey, authorization));

    [Fact]
    public void EndpointIsClosedWithoutAConfiguredKey() =>
        Assert.Equal(McpAccess.NotConfigured, Create(null).Check("anything", null));
}

public class SourceRegistryTests
{
    private readonly FakeTime _time = new();

    private SourceRegistry Registry(params ISourceProvider[] providers) =>
        new(providers, Media.Health(), _time, NullLogger<SourceRegistry>.Instance);

    [Fact]
    public async Task ServesAnOldListingAtOnceAndListsAgainOnlyWhenRefreshed()
    {
        var provider = new StaticProvider(new FakeSource("prowlarr:1"));
        using var registry = Registry(provider);

        await registry.GetSourcesAsync(CancellationToken.None);
        await registry.GetSourcesAsync(CancellationToken.None);
        Assert.Equal(1, provider.Calls);

        // Older than five minutes: still served, without asking the provider.
        _time.Now += TimeSpan.FromMinutes(6);
        provider.Sources.Add(new FakeSource("prowlarr:2"));
        Assert.Equal(["prowlarr:1"], (await registry.GetSourcesAsync(CancellationToken.None)).Select(s => s.Id));
        Assert.Equal(1, provider.Calls);

        await registry.RefreshAsync(CancellationToken.None);
        Assert.Equal(["prowlarr:1", "prowlarr:2"], (await registry.GetSourcesAsync(CancellationToken.None)).Select(s => s.Id));

        // A fresh listing is not made again.
        await registry.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task TryGetSourcesNeverWaitsForAListing()
    {
        var provider = new StaticProvider(new FakeSource("prowlarr:1"));
        using var registry = Registry(provider);

        Assert.False(registry.TryGetSources(out _));
        Assert.Equal(0, provider.Calls);

        await registry.RefreshAsync(CancellationToken.None);
        Assert.True(registry.TryGetSources(out var sources));
        Assert.Equal("prowlarr:1", Assert.Single(sources).Id);
    }

    [Fact]
    public async Task KeepsLastKnownSourcesWhileAProviderFails()
    {
        var provider = new StaticProvider(new FakeSource("prowlarr:1"));
        using var registry = Registry(provider);

        await registry.GetSourcesAsync(CancellationToken.None);
        Assert.Empty(registry.ProviderErrors);

        _time.Now += TimeSpan.FromMinutes(6);
        provider.Failure = new HttpRequestException("down");
        await registry.RefreshAsync(CancellationToken.None);

        Assert.Equal("prowlarr:1", Assert.Single(await registry.GetSourcesAsync(CancellationToken.None)).Id);
        Assert.NotNull(await registry.FindAsync("PROWLARR:1", CancellationToken.None));
        Assert.Equal(new SourceProviderError("static", "down", 1), Assert.Single(registry.ProviderErrors));

        _time.Now += TimeSpan.FromMinutes(6);
        provider.Failure = null;
        await registry.RefreshAsync(CancellationToken.None);

        Assert.Empty(registry.ProviderErrors);
    }

    [Fact]
    public async Task AsksAllProvidersAtOnce()
    {
        var first = new GatedProvider("first");
        var second = new GatedProvider("second");
        using var registry = Registry(first, second);

        var listing = registry.GetSourcesAsync(CancellationToken.None);

        // The second is asked while the first has not answered yet.
        Assert.True(first.Asked && second.Asked);
        second.Answer();
        first.Answer();

        Assert.Equal(["first:1", "second:1"], (await listing).Select(s => s.Id));
    }

    [Fact]
    public async Task RunListsAtStartAndWhenSomeoneFindsTheListingOld()
    {
        var provider = new StaticProvider(new FakeSource("prowlarr:1"));
        using var registry = Registry(provider);
        using var stop = new CancellationTokenSource();

        var run = registry.RunAsync(stop.Token);
        await WaitUntil(() => provider.Calls == 1);

        await registry.GetSourcesAsync(CancellationToken.None);
        Assert.Equal(1, provider.Calls);

        _time.Now += TimeSpan.FromMinutes(6);
        await registry.GetSourcesAsync(CancellationToken.None);
        await WaitUntil(() => provider.Calls == 2);

        await stop.CancelAsync();
        await run;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>A provider that answers only when told to.</summary>
    private sealed class GatedProvider(string name) : ISourceProvider
    {
        private readonly TaskCompletionSource _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Asked { get; private set; }

        public string Name => name;

        public void Answer() => _answer.SetResult();

        public async Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken)
        {
            Asked = true;
            await _answer.Task.WaitAsync(cancellationToken);
            return [new FakeSource($"{name}:1")];
        }
    }
}
