using Convy.Services.Downloads;
using Convy.Services.Jobs;

namespace Convy.Services.Tests;

public class JobStatusEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(30);

    private static JobRecord Job(JobStatus status, long lastBytes = 0, DateTimeOffset? lastProgress = null) => new()
    {
        Id = 1,
        Provider = DownloadProviders.QBittorrent,
        ItemRef = "h1",
        Category = "movies",
        Title = "Movie",
        Status = status,
        LastDownloadedBytes = lastBytes,
        LastProgressAt = lastProgress ?? T0,
        CreatedAt = T0,
        UpdatedAt = T0,
    };

    private static DownloadItem Item(DownloadState state, long downloaded = 0, string? error = null) =>
        FakeDownloader.Item("h1", state, downloaded: downloaded) with { Error = error };

    [Theory]
    [InlineData(DownloadState.Queued, JobStatus.Queued)]
    [InlineData(DownloadState.Paused, JobStatus.Queued)]
    [InlineData(DownloadState.Downloading, JobStatus.Downloading)]
    [InlineData(DownloadState.Completed, JobStatus.Placing)]
    [InlineData(DownloadState.Failed, JobStatus.Failed)]
    public void MapsDownloaderStates(DownloadState state, JobStatus expected) =>
        Assert.Equal(expected, JobStatusEvaluator.Observe(Job(JobStatus.Queued), Item(state), T0, StalledAfter).Status);

    [Fact]
    public void NoProgressForTooLongIsStalled()
    {
        var job = Job(JobStatus.Downloading, lastBytes: 50, lastProgress: T0);

        Assert.Equal(JobStatus.Downloading,
            JobStatusEvaluator.Observe(job, Item(DownloadState.Downloading, 50), T0 + TimeSpan.FromMinutes(29), StalledAfter).Status);
        Assert.Equal(JobStatus.Stalled,
            JobStatusEvaluator.Observe(job, Item(DownloadState.Downloading, 50), T0 + TimeSpan.FromMinutes(31), StalledAfter).Status);
    }

    [Fact]
    public void ProgressResumesAStalledJob()
    {
        var now = T0 + TimeSpan.FromHours(2);
        var observation = JobStatusEvaluator.Observe(
            Job(JobStatus.Stalled, lastBytes: 50, lastProgress: T0), Item(DownloadState.Downloading, 60), now, StalledAfter);

        Assert.Equal(JobStatus.Downloading, observation.Status);
        Assert.Equal(60, observation.DownloadedBytes);
        Assert.Equal(now, observation.LastProgressAt);
    }

    [Fact]
    public void RecoverableClientErrorIsAStallNotAFailure()
    {
        var observation = JobStatusEvaluator.Observe(
            Job(JobStatus.Downloading), Item(DownloadState.Errored, error: "qBittorrent reports state MissingFiles."), T0, StalledAfter);

        Assert.Equal(JobStatus.Stalled, observation.Status);
        Assert.Equal("qBittorrent reports state MissingFiles.", observation.Error);
    }

    [Fact]
    public void TimeInAQueueIsNotAStall()
    {
        var queuedSince = T0;
        var job = Job(JobStatus.Queued, lastBytes: 0, lastProgress: queuedSince);

        // An hour in the remote queue, then the transfer starts with zero bytes.
        var stillQueued = JobStatusEvaluator.Observe(job, Item(DownloadState.Queued), T0 + TimeSpan.FromHours(1), StalledAfter);
        Assert.Equal(JobStatus.Queued, stillQueued.Status);

        var started = JobStatusEvaluator.Observe(
            job with { LastProgressAt = stillQueued.LastProgressAt }, Item(DownloadState.Downloading),
            T0 + TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1), StalledAfter);
        Assert.Equal(JobStatus.Downloading, started.Status);
    }

    [Fact]
    public void RemovedItemFailsTheJob()
    {
        var observation = JobStatusEvaluator.Observe(Job(JobStatus.Downloading), null, T0, StalledAfter);

        Assert.Equal(JobStatus.Failed, observation.Status);
        Assert.Contains("removed", observation.Error);
    }

    [Fact]
    public void DownloaderErrorIsReported()
    {
        var observation = JobStatusEvaluator.Observe(
            Job(JobStatus.Downloading), Item(DownloadState.Failed, error: "Peer rejected 2 file(s)"), T0, StalledAfter);

        Assert.Equal("Peer rejected 2 file(s)", observation.Error);
    }

    [Fact]
    public void PlacingJobStaysPlacingWhileFilesMove()
    {
        Assert.Equal(JobStatus.Placing,
            JobStatusEvaluator.Observe(Job(JobStatus.Placing), Item(DownloadState.Unknown), T0, StalledAfter).Status);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void TerminalJobsNeverChange(JobStatus status) =>
        Assert.Equal(status, JobStatusEvaluator.Observe(Job(status), null, T0, StalledAfter).Status);
}
