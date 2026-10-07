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
}
