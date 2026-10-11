using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public sealed class JobServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new();
    private readonly FakeRules _rules = new()
    {
        Yaml =
            """
            rules:
              - name: movies
                condition: "Category == Movies"
                path: /data/media/movies
            """,
    };
    private readonly FakeFileSystem _fs = new();
    private readonly FakeDownloader _downloader = new();
    private readonly RecordingJobEvents _events = new();
    private readonly JobOptions _options = new();
    private readonly EfJobStore _store;
    private readonly JobService _service;

    public JobServiceTests()
    {
        _store = new EfJobStore(_db);
        _downloader.OnAdd = p => FakeDownloader.Item(((TorrentPayload)p).InfoHash, DownloadState.Queued, category: "Movies");
        _service = new JobService(
            new DownloaderResolver([_downloader]),
            _rules,
            _store,
            new JobTransitions(_store, _events),
            _fs,
            new StaticOptions<JobOptions>(_options),
            _time,
            NullLogger<JobService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static StartJobRequest Request(string? subpath = "Movie (2019)", long? size = 1000) => new()
    {
        Payload = new TorrentPayload("abc", "magnet:?xt=urn:btih:abc", null),
        Selection = FileSelection.All,
        Category = "movies",
        ClientCategory = "Movies",
        Subpath = subpath,
        Title = "Movie.2019.2160p",
        SizeBytes = size,
        FileCount = 1,
        ResultId = "r_1",
        SourceId = "prowlarr:12",
    };

    [Fact]
    public async Task StartAddsTheDownloadAndForecastsThePlacement()
    {
        var result = await _service.StartAsync(Request(), CancellationToken.None);

        var (payload, _, options) = Assert.Single(_downloader.Added);
        Assert.Equal("abc", ((TorrentPayload)payload).InfoHash);
        Assert.Equal("Movies", options.Category);

        Assert.Equal("j_1", result.Job.JobId);
        Assert.Equal(JobStatus.Queued, result.Job.Status);
        Assert.Equal("movies", result.Rule);
        Assert.Equal("/data/media/movies/Movie (2019)", FakeFileSystem.Norm(result.ExpectedPath!));

        var stored = await _store.GetAsync(result.Job.Id, CancellationToken.None);
        Assert.Equal("Movie (2019)", stored!.Subpath);
        Assert.Equal("movies", stored.Rule);

        var created = Assert.Single(_events.Changes);
        Assert.Null(created.PreviousStatus);
        Assert.Equal("movies", created.Job.Rule);                       // the first event is complete
        Assert.Equal(result.ExpectedPath, created.Job.TargetPath);
    }

    [Fact]
    public async Task InvalidSubpathIsRejectedBeforeDownloading()
    {
        var ex = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.StartAsync(Request("../etc"), CancellationToken.None));

        Assert.Contains("..", ex.Message);
        Assert.Empty(_downloader.Added);
    }

    [Fact]
    public async Task SubpathThroughSymlinkOutsideTheRulePathIsRejected()
    {
        _fs.Symlinks["/data/media/movies/Evil"] = "/etc";

        await Assert.ThrowsAsync<ConvyRequestException>(() => _service.StartAsync(Request("Evil"), CancellationToken.None));
        Assert.Empty(_downloader.Added);
    }

    [Fact]
    public async Task JobsAboveTheSizeLimitAreRejected()
    {
        _options.MaxSizeGb = 1;

        var ex = await Assert.ThrowsAsync<ConvyRequestException>(
            () => _service.StartAsync(Request(size: 2L * 1024 * 1024 * 1024), CancellationToken.None));

        Assert.Contains("limit", ex.Message);
        Assert.Empty(_downloader.Added);
    }

    [Fact]
    public async Task NotEnoughFreeSpaceIsRejected()
    {
        _fs.FreeSpace = 500;

        var ex = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.StartAsync(Request(), CancellationToken.None));

        Assert.Contains("free space", ex.Message);
        Assert.Empty(_downloader.Added);
    }

    [Fact]
    public async Task CancelStopsTheDownloadAndKeepsItsData()
    {
        var started = await _service.StartAsync(Request(), CancellationToken.None);

        var cancelled = await _service.CancelAsync(started.Job.JobId, CancellationToken.None);

        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.Equal("abc", Assert.Single(_downloader.Cancelled));
        Assert.Equal(JobStatus.Cancelled, _events.Changes[^1].Job.Status);
        Assert.Equal(JobStatus.Queued, _events.Changes[^1].PreviousStatus);

        var again = await Assert.ThrowsAsync<ConvyRequestException>(
            () => _service.CancelAsync(started.Job.JobId, CancellationToken.None));
        Assert.Contains("already cancelled", again.Message);
    }

    [Fact]
    public async Task SecondDownloadOfAnActiveItemIsRejected()
    {
        var first = await _service.StartAsync(Request(), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.StartAsync(Request(), CancellationToken.None));

        Assert.Contains(first.Job.JobId, ex.Message);
        Assert.Single(_downloader.Added);
    }

    [Fact]
    public async Task FinishedItemCanBeDownloadedAgain()
    {
        var first = await _service.StartAsync(Request(), CancellationToken.None);
        await _service.CancelAsync(first.Job.JobId, CancellationToken.None);

        var second = await _service.StartAsync(Request(), CancellationToken.None);

        Assert.NotEqual(first.Job.Id, second.Job.Id);
    }

    [Fact]
    public async Task CancelThatCannotStopTheDownloadKeepsTheJobActive()
    {
        var started = await _service.StartAsync(Request(), CancellationToken.None);
        _downloader.CancelFailure = new HttpRequestException("qBittorrent is down");

        await Assert.ThrowsAsync<HttpRequestException>(() => _service.CancelAsync(started.Job.JobId, CancellationToken.None));

        Assert.Equal(JobStatus.Queued, (await _store.GetAsync(started.Job.Id, CancellationToken.None))!.Status);
        Assert.DoesNotContain(_events.Changes, c => c.Job.Status == JobStatus.Cancelled);
    }

    [Theory]
    [InlineData("j_99", "does not exist")]
    [InlineData("42", "not a job id")]
    public async Task CancelUnknownJobExplainsWhy(string jobId, string reason)
    {
        var ex = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.CancelAsync(jobId, CancellationToken.None));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public async Task ListShowsTheLiveStateFromTheDownloader()
    {
        var started = await _service.StartAsync(Request(), CancellationToken.None);
        _downloader.Items["abc"] = FakeDownloader.Item("abc", DownloadState.Downloading, size: 1000, downloaded: 250)
            with { DownloadSpeed = 4096 };

        var view = Assert.Single(await _service.ListAsync(null, null, CancellationToken.None));

        Assert.Equal(started.Job.Id, view.Job.Id);
        Assert.Equal(JobStatus.Downloading, view.Status);
        Assert.Equal(0.25, view.Progress);
        Assert.Equal(4096, view.SpeedBytesPerSecond);

        Assert.Single(await _service.ListAsync(JobStatus.Downloading, null, CancellationToken.None));
        Assert.Empty(await _service.ListAsync(JobStatus.Queued, null, CancellationToken.None));
    }

    [Fact]
    public async Task ListKeepsTheStoredStatusWhenTheDownloaderIsUnreachable()
    {
        await _service.StartAsync(Request(), CancellationToken.None);
        _downloader.Unreachable = true;

        var view = Assert.Single(await _service.ListAsync(null, null, CancellationToken.None));

        Assert.Equal(JobStatus.Queued, view.Status);
        Assert.Contains("unreachable", view.Error);
    }

    [Fact]
    public async Task ListAsksAnUnreachableDownloaderOnlyOnce()
    {
        await _service.StartAsync(Request(), CancellationToken.None);
        var second = Request() with { Payload = new TorrentPayload("def", "magnet:?xt=urn:btih:def", null) };
        await _service.StartAsync(second, CancellationToken.None);
        _downloader.Unreachable = true;
        var reads = _downloader.ItemReads;

        var views = await _service.ListAsync(null, null, CancellationToken.None);

        Assert.Equal(2, views.Count);
        Assert.All(views, v => Assert.Contains("unreachable", v.Error));
        Assert.Equal(reads + 1, _downloader.ItemReads);
    }

    private static StartJobRequest Release(string hash, string title, string? subpath = null, long? size = 1000) =>
        Request(subpath, size) with { Payload = new TorrentPayload(hash, $"magnet:?xt=urn:btih:{hash}", null), Title = title };

    [Fact]
    public async Task SeveralReleasesMakeOneJobAnnouncedOnce()
    {
        var result = await _service.StartAsync(
            [Release("abc", "Movie One", "Movie One (2019)"), Release("def", "Movie Two", "Movie Two (2020)")],
            CancellationToken.None);

        Assert.Equal("j_1", result.Job.JobId);
        Assert.Equal(2, result.Job.Releases.Count);
        Assert.All(result.Job.Releases, r => Assert.Equal(result.Job.Id, r.GroupId));
        Assert.All(result.Job.Releases, r => Assert.Equal("j_1", r.JobId));
        Assert.Equal("Movie One (+1 more)", result.Job.Title);
        Assert.Equal("/data/media/movies", FakeFileSystem.Norm(result.ExpectedPath!));
        Assert.Equal("movies", result.Rule);
        Assert.Empty(result.Failed);
        Assert.Equal(["abc", "def"], _downloader.Added.Select(a => ((TorrentPayload)a.Payload).InfoHash));

        var created = Assert.Single(_events.Changes);
        Assert.Null(created.PreviousStatus);
        Assert.Equal(2, created.Job.Releases.Count);
    }

    [Fact]
    public async Task AProblemWithAnyReleaseRejectsTheWholeRequestBeforeAdding()
    {
        var invalid = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.StartAsync(
            [Release("abc", "Movie One"), Release("def", "Movie Two", "../evil")], CancellationToken.None));
        Assert.StartsWith("'Movie Two': ", invalid.Message);

        var twin = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.StartAsync(
            [Release("abc", "Movie One"), Release("abc", "Movie One again")], CancellationToken.None));
        Assert.Contains("same download as 'Movie One'", twin.Message);

        _options.MaxSizeGb = 1.5;
        var tooBig = await Assert.ThrowsAsync<ConvyRequestException>(() => _service.StartAsync(
            [Release("abc", "One", size: 1L << 30), Release("def", "Two", size: 1L << 30)], CancellationToken.None));
        Assert.Contains("above the 1.5 GiB limit per job", tooBig.Message);

        Assert.Empty(_downloader.Added);
        Assert.Empty(_events.Changes);
    }

    [Fact]
    public async Task AReleaseTheDownloaderRefusesIsReportedAndTheJobKeepsTheOthers()
    {
        _downloader.OnAdd = p => ((TorrentPayload)p).InfoHash == "def"
            ? throw new InvalidOperationException("refused")
            : FakeDownloader.Item(((TorrentPayload)p).InfoHash, DownloadState.Queued, category: "Movies");

        var result = await _service.StartAsync([Release("abc", "One"), Release("def", "Two")], CancellationToken.None);

        Assert.Equal("abc", Assert.Single(result.Job.Releases).ItemRef);
        var failed = Assert.Single(result.Failed);
        Assert.Equal(("Two", "refused"), (failed.Request.Title, failed.Error));

        _downloader.OnAdd = _ => throw new InvalidOperationException("refused");
        var none = await Assert.ThrowsAsync<ConvyRequestException>(
            () => _service.StartAsync([Release("ghi", "Three"), Release("jkl", "Four")], CancellationToken.None));
        Assert.Contains("No release could be added", none.Message);
    }

    [Fact]
    public async Task CancelStopsEveryUnfinishedReleaseWithOneEvent()
    {
        var started = await _service.StartAsync([Release("abc", "One"), Release("def", "Two")], CancellationToken.None);
        _events.Changes.Clear();

        var cancelled = await _service.CancelAsync(started.Job.JobId, CancellationToken.None);

        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.All(cancelled.Releases, r => Assert.Equal(JobStatus.Cancelled, r.Status));
        Assert.Equal(["abc", "def"], _downloader.Cancelled);
        var change = Assert.Single(_events.Changes);
        Assert.Equal((JobStatus.Cancelled, JobStatus.Queued), (change.Job.Status, change.PreviousStatus));
    }

    [Fact]
    public async Task ListCombinesTheLiveStateOfTheReleases()
    {
        await _service.StartAsync([Release("abc", "One"), Release("def", "Two")], CancellationToken.None);
        _downloader.Items["abc"] = FakeDownloader.Item("abc", DownloadState.Downloading, size: 1000, downloaded: 1000)
            with { DownloadSpeed = 0 };
        _downloader.Items["def"] = FakeDownloader.Item("def", DownloadState.Downloading, size: 3000, downloaded: 1000)
            with { DownloadSpeed = 512 };

        var view = Assert.Single(await _service.ListAsync(null, null, CancellationToken.None));

        Assert.Equal(JobStatus.Downloading, view.Status);
        Assert.Equal(0.5, view.Progress);
        Assert.Equal(2000, view.DownloadedBytes);
        Assert.Equal(512, view.SpeedBytesPerSecond);
        Assert.Equal(["One", "Two"], view.Releases.Select(r => r.Release.Title));
        Assert.Single(await _service.ListAsync(JobStatus.Downloading, null, CancellationToken.None));
        Assert.Equal(1, (await _store.CountByStatusAsync(CancellationToken.None))[JobStatus.Queued]);
    }
}
