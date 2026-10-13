using Convy.Services.Diagnostics;
using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Ui;

namespace Convy.Services.Tests;

public sealed class UiLogServiceTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly EfJobStore _jobs;
    private readonly LogBuffer _buffer = new();
    private readonly UiLogService _service;

    public UiLogServiceTests()
    {
        _jobs = new EfJobStore(_db);
        _service = new UiLogService(_buffer, _jobs);
    }

    public void Dispose() => _db.Dispose();

    private Task<JobRecord> AddJob(string provider, string itemRef) => _jobs.CreateAsync(new JobRecord
    {
        Id = 0,
        Provider = provider,
        ItemRef = itemRef,
        Category = "books",
        Title = itemRef,
        Status = JobStatus.Downloading,
        LastProgressAt = At,
        CreatedAt = At,
        UpdatedAt = At,
    }, CancellationToken.None);

    private void Log(string message, LogSeverity level = LogSeverity.Information, params (string Key, object? Value)[] properties) =>
        _buffer.Add(At, level, "Test", message, null, properties.ToDictionary(p => p.Key, p => p.Value));

    [Fact]
    public async Task TellsTheJobOfEveryEntryAlsoOfThoseThatOnlyKnowTheItem()
    {
        var book = await AddJob(DownloadProviders.Opds, "flibusta/b598916");
        var movie = await AddJob(DownloadProviders.QBittorrent, "abc");

        Log("Queued Dickens - Pickwick (fb2).", properties: [(LogScopes.Provider, "opds"), (LogScopes.ItemRef, "flibusta/b598916")]);
        Log("Downloaded Dickens - Pickwick.fb2 into /books/flibusta/b598916/Dickens - Pickwick.", properties: [(LogScopes.ItemRef, "flibusta/b598916")]);
        Log("Placement attempt 1 of 10 failed.", LogSeverity.Warning, (LogScopes.JobId, book.JobId), (LogScopes.ItemRef, "flibusta/b598916"));
        Log("Linked the movie.", properties: [(LogScopes.JobId, movie.JobId)]);
        Log("Sync cycle done.");

        var all = await _service.ReadAsync(0, LogSeverity.Verbose, null, null, 100, CancellationToken.None);
        Assert.Equal([book.JobId, book.JobId, book.JobId, movie.JobId, null], all.Entries.Select(e => e.JobId));

        // The story of one job, by its id or its number.
        var story = await _service.ReadAsync(0, LogSeverity.Verbose, null, book.JobId, 100, CancellationToken.None);
        Assert.Equal(3, story.Entries.Count);
        Assert.Equal(3, (await _service.ReadAsync(0, LogSeverity.Verbose, null, book.JobId[2..], 100, CancellationToken.None)).Entries.Count);
        Assert.Single((await _service.ReadAsync(0, LogSeverity.Warning, null, book.JobId, 100, CancellationToken.None)).Entries);

        // The text filter also finds entries by job id.
        Assert.Single((await _service.ReadAsync(0, LogSeverity.Verbose, movie.JobId, null, 100, CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task KeepsTheStructuredProperties()
    {
        Log("Job j_7: placement attempt 2 of 10 failed.", LogSeverity.Warning, (LogScopes.JobId, "j_7"), ("Attempt", 2));

        var entry = Assert.Single((await _service.ReadAsync(0, LogSeverity.Verbose, null, null, 100, CancellationToken.None)).Entries);

        Assert.Equal("j_7", entry.JobId);
        Assert.Equal(2, entry.Properties!["Attempt"]);
    }
}
