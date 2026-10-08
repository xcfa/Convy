using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Linking;
using Convy.Services.Storage;
using Convy.Services.Sync;
using Convy.Services.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public sealed class SyncCycleServiceTests : IDisposable
{
    private const string Rules =
        """
        rules:
          - name: movies
            condition: "Category == Movies"
            path: /data/media/movies
        """;

    private readonly TestDb _db = new();
    private readonly FakeTime _time = new();
    private readonly FakeRules _rules = new() { Yaml = Rules };
    private readonly FakeFileSystem _fs = new();
    private readonly FakeDownloader _downloader = new();
    private readonly RecordingWebhooks _webhooks = new();
    private readonly RecordingJobEvents _events = new();
    private readonly StorageLayoutStatus _storage = new();
    private readonly EfJobStore _jobs;
    private readonly JobOptions _jobOptions = new() { MaxPlacementAttempts = 3 };
    private readonly SyncCycleService _cycle;

    public SyncCycleServiceTests()
    {
        _jobs = new EfJobStore(_db);
        var resolver = new DownloaderResolver([_downloader]);
        _cycle = new SyncCycleService(
            resolver,
            _db,
            _rules,
            new DownloadStateTracker(new EfDownloadStateStore(_db)),
            new FileLinkingService(_fs, NullLogger<FileLinkingService>.Instance),
            _webhooks,
            _jobs,
            new JobTransitions(_jobs, _events),
            new StaticOptions<JobOptions>(_jobOptions),
            _fs,
            new StorageLayoutValidator(resolver, _fs, _storage, _time, NullLogger<StorageLayoutValidator>.Instance),
            _time,
            NullLogger<SyncCycleService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private Task Run() => _cycle.RunAsync(CancellationToken.None);

    private void AddCompletedItem(string itemRef, string? category, params string[] files)
    {
        foreach (var file in files)
            _fs.AddFile($"/data/downloads/{file}");

        _downloader.Items[itemRef] = FakeDownloader.Item(
            itemRef, DownloadState.Completed, category: category, files: files.Select(f => FakeDownloader.Done(f)).ToArray());
    }

    private async Task<JobRecord> AddJob(
        string itemRef, string? clientCategory, string? subpath, JobStatus status = JobStatus.Downloading,
        IReadOnlyList<string>? selection = null) =>
        await _jobs.CreateAsync(new JobRecord
        {
            Id = 0,
            Provider = DownloadProviders.QBittorrent,
            ItemRef = itemRef,
            Category = "movies",
            ClientCategory = clientCategory,
            Subpath = subpath,
            SelectedFiles = selection,
            Title = itemRef,
            Status = status,
            LastProgressAt = _time.Now,
            CreatedAt = _time.Now,
            UpdatedAt = _time.Now,
        }, CancellationToken.None);

    private async Task<JobRecord> Reload(JobRecord job) => (await _jobs.GetAsync(job.Id, CancellationToken.None))!;

    private string[] LinkedDestinations() => _fs.Links.Select(l => l.Destination).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task ManualItemIsPlacedByRulesAsBefore()
    {
        AddCompletedItem("h1", "Movies", "Movie.2019/movie.mkv");

        await Run();

        Assert.Equal(["/data/media/movies/Movie.2019/movie.mkv"], LinkedDestinations());
        var batch = Assert.Single(_webhooks.Batches);
        Assert.Equal("movies", Assert.Single(batch.Linked).RuleName);
        Assert.Empty(_events.Changes);
    }

    [Fact]
    public async Task ManualItemWithoutRuleIsSkipped()
    {
        AddCompletedItem("h1", "Other", "x.mkv");

        await Run();

        Assert.Empty(_fs.Links);
        Assert.Empty(_webhooks.Batches);
    }

    [Fact]
    public async Task JobWithRuleAndSubpathReplacesTheRootFolder()
    {
        AddCompletedItem("h1", "Manual", "Movie.2019.2160p.WEB-DL/movie.mkv", "Movie.2019.2160p.WEB-DL/Subs/en.srt");
        var job = await AddJob("h1", "Movies", "Movie (2019)");

        await Run();

        Assert.Equal(
            ["/data/media/movies/Movie (2019)/Subs/en.srt", "/data/media/movies/Movie (2019)/movie.mkv"],
            LinkedDestinations());

        var done = await Reload(job);
        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.Equal("movies", done.Rule);
        Assert.Equal("/data/media/movies/Movie (2019)", FakeFileSystem.Norm(done.TargetPath!));

        var linked = Assert.Single(Assert.Single(_webhooks.Batches).Linked);
        Assert.Equal("movies", linked.RuleName);
        Assert.Equal(job.JobId, linked.Properties["job_id"]);
        Assert.Equal("qbittorrent", linked.Properties["provider"]);
        Assert.Equal("Movies", linked.Properties["category"]);

        // downloading -> placing -> completed, the last one carrying the placed files.
        Assert.Equal([JobStatus.Placing, JobStatus.Completed], _events.Changes.Select(c => c.Job.Status));
        Assert.Equal(["Subs/en.srt", "movie.mkv"], _events.Changes[^1].PlacedFiles!.Order(StringComparer.Ordinal));

        await using var db = _db.CreateDbContext();
        var entries = await db.FileEntries.OrderBy(e => e.FilePath).ToListAsync();
        Assert.Equal("Movie.2019.2160p.WEB-DL/Subs/en.srt", entries[0].FilePath);
        Assert.Equal("/data/media/movies/Movie (2019)/Subs/en.srt", FakeFileSystem.Norm(entries[0].TargetPath));
    }

    [Fact]
    public async Task JobWithRuleWithoutSubpathKeepsTheStructure()
    {
        AddCompletedItem("h1", null, "Movie.2019/movie.mkv");
        var job = await AddJob("h1", "Movies", subpath: null);

        await Run();

        Assert.Equal(["/data/media/movies/Movie.2019/movie.mkv"], LinkedDestinations());
        Assert.Equal(JobStatus.Completed, (await Reload(job)).Status);
    }

    [Fact]
    public async Task JobWithoutRuleButWithSubpathStaysInTheDownloadDirectory()
    {
        AddCompletedItem("h1", null, "Show.S01/e1.mkv");
        var job = await AddJob("h1", "Series", "Show (2019)");

        await Run();

        Assert.Equal(["/data/downloads/Show (2019)/e1.mkv"], LinkedDestinations());
        Assert.Equal(JobStatus.Completed, (await Reload(job)).Status);
    }

    [Fact]
    public async Task JobWithoutRuleAndSubpathLeavesFilesInPlace()
    {
        AddCompletedItem("h1", null, "x.mkv");
        var job = await AddJob("h1", "Other", subpath: null);

        await Run();

        Assert.Empty(_fs.Links);
        var done = await Reload(job);
        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.Equal("/data/downloads", done.TargetPath);
    }

    [Fact]
    public async Task OnlySelectedCompleteFilesArePlaced()
    {
        _fs.AddFile("/data/downloads/Show/Season 01/e1.mkv");
        _fs.AddFile("/data/downloads/Show/Season 02/e1.mkv");
        _downloader.Items["h1"] = FakeDownloader.Item("h1", DownloadState.Completed, files:
        [
            FakeDownloader.Done("Show/Season 01/e1.mkv") with { Selected = false },
            FakeDownloader.Done("Show/Season 02/e1.mkv"),
            new DownloadFile("Show/Season 02/sample.mkv", 1, 0.2, true),
        ]);
        await AddJob("h1", "Movies", "Show (2019)", selection: ["Season 02/e1.mkv", "Season 02/sample.mkv"]);

        await Run();

        Assert.Equal(["/data/media/movies/Show (2019)/Season 02/e1.mkv"], LinkedDestinations());
    }

    [Fact]
    public async Task FailedPlacementIsRetriedUntilAttemptsRunOut()
    {
        AddCompletedItem("h1", null, "Movie/movie.mkv");
        _fs.FailingSources.Add("/data/downloads/Movie/movie.mkv");
        var job = await AddJob("h1", "Movies", "Movie (2019)");

        await Run();
        var placing = await Reload(job);
        Assert.Equal(JobStatus.Placing, placing.Status);
        Assert.Equal(1, placing.PlacementAttempts);
        Assert.Contains("EXDEV", placing.Error);

        await Run();
        await Run();

        var failed = await Reload(job);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains("3 attempt", failed.Error);

        // A failed job is not retried any more.
        _fs.FailingSources.Clear();
        await Run();
        Assert.Empty(_fs.Links);
    }

    [Fact]
    public async Task RemovedDownloadFailsTheJob()
    {
        var job = await AddJob("gone", "Movies", null);

        await Run();

        var failed = await Reload(job);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains("removed", failed.Error);
    }

    [Fact]
    public async Task ProgressUpdatesTheJobStatus()
    {
        _downloader.Items["h1"] = FakeDownloader.Item("h1", DownloadState.Queued);
        var job = await AddJob("h1", "Movies", null, JobStatus.Queued);

        await Run();
        Assert.Equal(JobStatus.Queued, (await Reload(job)).Status);

        _downloader.Items["h1"] = FakeDownloader.Item("h1", DownloadState.Downloading, downloaded: 10);
        await Run();
        Assert.Equal(JobStatus.Downloading, (await Reload(job)).Status);

        _time.Now += TimeSpan.FromHours(1);
        await Run();
        Assert.Equal(JobStatus.Stalled, (await Reload(job)).Status);

        Assert.Equal([JobStatus.Downloading, JobStatus.Stalled], _events.Changes.Select(c => c.Job.Status));
    }

    [Fact]
    public async Task SubpathEscapingThroughASymlinkFailsTheJob()
    {
        AddCompletedItem("h1", null, "Movie/movie.mkv");
        _fs.Symlinks["/data/media/movies/Evil"] = "/etc";
        var job = await AddJob("h1", "Movies", "Evil");

        await Run();

        Assert.Empty(_fs.Links);
        Assert.Equal(JobStatus.Failed, (await Reload(job)).Status);
    }

    [Fact]
    public async Task CancelledJobItemIsTreatedAsManual()
    {
        AddCompletedItem("h1", "Movies", "Movie/movie.mkv");
        await AddJob("h1", "Series", "Ignored", JobStatus.Cancelled);

        await Run();

        Assert.Equal(["/data/media/movies/Movie/movie.mkv"], LinkedDestinations());
    }

    [Fact]
    public async Task ReusedTorrentAlreadyProcessedIsStillPlacedForTheJob()
    {
        AddCompletedItem("h1", "Other", "Movie/movie.mkv");
        await Run(); // no rule for "Other": skipped as a manual item

        var job = await AddJob("h1", "Movies", "Movie (2019)", JobStatus.Queued);
        await Run();

        Assert.Equal(["/data/media/movies/Movie (2019)/movie.mkv"], LinkedDestinations());
        Assert.Equal(JobStatus.Completed, (await Reload(job)).Status);
    }

    [Fact]
    public async Task TorrentPlacedManuallyBeforeIsLinkedAgainToTheJobTarget()
    {
        AddCompletedItem("h1", "Movies", "Movie/movie.mkv");
        await Run(); // placed by the rule: /data/media/movies/Movie/movie.mkv

        var job = await AddJob("h1", "Movies", "Movie (2019)", JobStatus.Queued);
        await Run();

        Assert.Equal(
            ["/data/media/movies/Movie (2019)/movie.mkv", "/data/media/movies/Movie/movie.mkv"],
            LinkedDestinations());
        Assert.Equal(JobStatus.Completed, (await Reload(job)).Status);
    }

    [Fact]
    public async Task JobWithoutCompleteSelectedFilesFailsInsteadOfCompleting()
    {
        _downloader.Items["h1"] = FakeDownloader.Item("h1", DownloadState.Completed,
            files: [FakeDownloader.Done("Show/e1.mkv")]);
        var job = await AddJob("h1", "Movies", "Show", selection: ["e9.mkv"]);

        await Run();

        Assert.Empty(_fs.Links);
        var failed = await Reload(job);
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains("None of the selected files", failed.Error);
    }

    [Fact]
    public async Task UnsafePathsReportedByADownloaderAreNeverLinked()
    {
        _fs.AddFile("/data/downloads/Album/01.flac");
        _downloader.Items["h1"] = FakeDownloader.Item("h1", DownloadState.Completed, category: "Movies",
            files: [FakeDownloader.Done("Album/01.flac"), FakeDownloader.Done("../escape.flac")]);

        await Run();

        Assert.Equal(["/data/media/movies/Album/01.flac"], LinkedDestinations());
    }

    [Fact]
    public async Task StorageCheckReportsRulePathsOnAnotherMount()
    {
        _fs.Directories.Add("/data/downloads");
        _fs.Mounts["/data/downloads"] = "10";
        _fs.Mounts["/data/media"] = "11";

        await Run();

        var problem = Assert.Single(_storage.Problems);
        Assert.Contains("movies", problem);
        Assert.Contains("EXDEV", problem);
    }

    [Fact]
    public async Task StorageCheckReportsInvisibleDownloadDirectory()
    {
        await Run();

        Assert.Contains("not visible", Assert.Single(_storage.Problems));
    }

    [Fact]
    public async Task SoulseekJobUsesTheJobCategoryAndReplacesTheUserFolder()
    {
        var slskd = new FakeDownloader(DownloadProviders.Slskd, Sources.Protocol.Soulseek) { DownloadDirectory = "/data/slskd" };
        var resolver = new DownloaderResolver([slskd]);
        var cycle = new SyncCycleService(
            resolver, _db, _rules, new DownloadStateTracker(new EfDownloadStateStore(_db)),
            new FileLinkingService(_fs, NullLogger<FileLinkingService>.Instance), _webhooks, _jobs,
            new JobTransitions(_jobs, _events), new StaticOptions<JobOptions>(_jobOptions), _fs,
            new StorageLayoutValidator(resolver, _fs, _storage, _time, NullLogger<StorageLayoutValidator>.Instance),
            _time, NullLogger<SyncCycleService>.Instance);

        // A rule on Tags comes first: Soulseek items have no tags, so it must be skipped.
        _rules.Yaml =
            """
            rules:
              - name: untagged
                condition: "!Tags.Contains(skip)"
                path: /data/media/untagged
              - name: music
                condition: "Category == Music"
                path: /data/media/music
            """;

        _fs.AddFile("/data/slskd/2003 - Fallen/01.flac");
        _fs.AddFile("/data/slskd/2003 - Fallen/CD2/01.flac");
        _fs.AddFile("/data/slskd/2003 - Fallen/cover.jpg");
        slskd.Items["bob/album"] = new DownloadItem
        {
            Provider = DownloadProviders.Slskd,
            ItemRef = "bob/album",
            Name = "2003 - Fallen",
            SavePath = "/data/slskd",
            State = DownloadState.Completed,
            Files =
            [
                FakeDownloader.Done("2003 - Fallen/01.flac"),
                FakeDownloader.Done("2003 - Fallen/CD2/01.flac"),
                FakeDownloader.Done("2003 - Fallen/cover.jpg"),
            ],
            Properties = new Dictionary<string, object?> { ["Name"] = "2003 - Fallen", ["Username"] = "bob" },
        };

        var job = await _jobs.CreateAsync(new JobRecord
        {
            Id = 0, Provider = DownloadProviders.Slskd, ItemRef = "bob/album", Category = "music", ClientCategory = "Music",
            Subpath = "Evanescence/2003 - Fallen", Title = "Fallen", Status = JobStatus.Queued,
            LastProgressAt = _time.Now, CreatedAt = _time.Now, UpdatedAt = _time.Now,
        }, CancellationToken.None);

        await cycle.RunAsync(CancellationToken.None);

        Assert.Equal(
            [
                "/data/media/music/Evanescence/2003 - Fallen/01.flac",
                "/data/media/music/Evanescence/2003 - Fallen/CD2/01.flac",
                "/data/media/music/Evanescence/2003 - Fallen/cover.jpg",
            ],
            LinkedDestinations());
        var done = await Reload(job);
        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.Equal("music", done.Rule);
    }

    [Fact]
    public async Task UnreachableDownloaderDoesNotBreakTheCycle()
    {
        _downloader.Unreachable = true;

        await Run();

        Assert.Empty(_webhooks.Batches);
    }
}
