using Convy.Data.Context;
using Convy.Data.Entities;
using Convy.Services.Diagnostics;
using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Media;
using Convy.Services.Security;
using Convy.Services.Storage;
using Convy.Services.Sync;
using Convy.Services.Ui;
using Convy.Sources;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class LogBufferTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReturnsEntriesAfterTheGivenSequenceOldestFirst()
    {
        var buffer = new LogBuffer();
        buffer.Add(At, LogSeverity.Information, "A", "one", null);
        buffer.Add(At, LogSeverity.Information, "A", "two", null);
        buffer.Add(At, LogSeverity.Information, "A", "three", null);

        var page = buffer.Read(after: 1, LogSeverity.Verbose, null, limit: 100);

        Assert.Equal(["two", "three"], page.Entries.Select(e => e.Message));
        Assert.Equal([2L, 3L], page.Entries.Select(e => e.Sequence));
        Assert.Equal(3, page.LastSequence);
        Assert.Empty(buffer.Read(page.LastSequence, LogSeverity.Verbose, null, 100).Entries);
    }

    [Fact]
    public void FiltersByLevelAndTextInMessageCategoryOrException()
    {
        var buffer = new LogBuffer();
        buffer.Add(At, LogSeverity.Debug, "Sync", "debug detail", null);
        buffer.Add(At, LogSeverity.Information, "Convy.Services.Sync.SyncCycleService", "cycle done", null);
        buffer.Add(At, LogSeverity.Error, "Webhooks", "failed", "System.Net.Http.HttpRequestException: refused");
        buffer.Add(At, LogSeverity.Warning, "Jobs", "Job j_1 stalled", null);

        Assert.Equal(["cycle done", "failed", "Job j_1 stalled"],
            buffer.Read(0, LogSeverity.Information, null, 100).Entries.Select(e => e.Message));
        Assert.Equal(["failed", "Job j_1 stalled"],
            buffer.Read(0, LogSeverity.Warning, null, 100).Entries.Select(e => e.Message));
        Assert.Equal(["cycle done"], buffer.Read(0, LogSeverity.Verbose, "synccycle", 100).Entries.Select(e => e.Message));
        Assert.Equal(["failed"], buffer.Read(0, LogSeverity.Verbose, "REFUSED", 100).Entries.Select(e => e.Message));
        Assert.Equal(["Job j_1 stalled"], buffer.Read(0, LogSeverity.Verbose, "j_1", 100).Entries.Select(e => e.Message));
    }

    [Fact]
    public void LimitKeepsTheNewestEntries()
    {
        var buffer = new LogBuffer();
        for (var i = 1; i <= 10; i++)
        {
            buffer.Add(At, LogSeverity.Information, null, $"m{i}", null);
        }

        var page = buffer.Read(0, LogSeverity.Verbose, null, limit: 3);

        Assert.Equal(["m8", "m9", "m10"], page.Entries.Select(e => e.Message));
        Assert.Equal(10, page.LastSequence);
    }

    [Fact]
    public void DropsTheOldestEntriesBeyondTheCapacity()
    {
        var buffer = new LogBuffer(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(At, LogSeverity.Information, null, $"m{i}", null);
        }

        Assert.Equal(["m3", "m4", "m5"], buffer.Read(0, LogSeverity.Verbose, null, 100).Entries.Select(e => e.Message));
        Assert.Equal(["m5"], buffer.Read(4, LogSeverity.Verbose, null, 100).Entries.Select(e => e.Message));
    }
}

public class UiOptionsTests
{
    private static UiOptions Configured(Action<UiOptions>? change = null)
    {
        var options = new UiOptions
        {
            Oidc = new UiOidcOptions { Authority = "https://auth.example.com", ClientId = "convy", ClientSecret = "secret" },
        };
        change?.Invoke(options);
        return options;
    }

    [Fact]
    public void ModeIsOidcOnlyWhenTheClientIsComplete()
    {
        Assert.Equal(UiMode.Oidc, Configured().ResolveMode(out var problem));
        Assert.Null(problem);

        Assert.Equal(UiMode.Disabled, new UiOptions().ResolveMode(out problem));
        Assert.Contains("Ui:Oidc:Authority, Ui:Oidc:ClientId, Ui:Oidc:ClientSecret", problem);

        Assert.Equal(UiMode.Disabled, Configured(o => o.Oidc.ClientSecret = " ").ResolveMode(out problem));
        Assert.Contains("Ui:Oidc:ClientSecret", problem);
    }

    [Fact]
    public void ModeNoneOpensTheUiAndUnknownModesTurnItOff()
    {
        Assert.Equal(UiMode.Open, new UiOptions { Auth = "None" }.ResolveMode(out _));
        Assert.Equal(UiMode.Disabled, Configured(o => o.Auth = "basic").ResolveMode(out var problem));
        Assert.Contains("'basic'", problem);
    }

    [Theory]
    [InlineData("https://convy.example.com", "https://convy.example.com/")]
    [InlineData("https://convy.example.com/", "https://convy.example.com/")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080/")]
    public void PublicUrlGivesTheOrigin(string url, string expected)
    {
        Assert.True(new UiOptions { PublicUrl = url }.TryGetPublicOrigin(out var origin));
        Assert.Equal(expected, origin.ToString());
    }

    [Theory]
    [InlineData("https://convy.example.com", "https", "convy.example.com")]
    [InlineData("https://convy.example.com:443/", "https", "convy.example.com")]
    [InlineData("http://convy.example.com:80", "http", "convy.example.com")]
    [InlineData("https://convy.example.com:8443", "https", "convy.example.com:8443")]
    [InlineData("http://10.0.0.5:8080", "http", "10.0.0.5:8080")]
    [InlineData("http://[::1]:5000", "http", "[::1]:5000")]
    public void PublicHostLeavesOutTheDefaultPort(string url, string expectedScheme, string expectedHost)
    {
        Assert.True(new UiOptions { PublicUrl = url }.TryGetPublicHost(out var scheme, out var host));
        Assert.Equal((expectedScheme, expectedHost), (scheme, host));
    }

    [Theory]
    [InlineData("https://example.com/convy")]
    [InlineData("ftp://example.com")]
    [InlineData("convy.example.com")]
    public void PublicUrlWithAPathOrOtherSchemeTurnsTheUiOff(string url)
    {
        Assert.Equal(UiMode.Disabled, Configured(o => o.PublicUrl = url).ResolveMode(out var problem));
        Assert.Contains("Ui:PublicUrl", problem);
    }

    [Fact]
    public void DefaultScopesAskForGroupsAndConfiguredOnesAlwaysIncludeOpenid()
    {
        Assert.Equal(["openid", "profile", "email", "groups"], new UiOidcOptions().EffectiveScopes);
        Assert.Equal(["profile", "openid"], new UiOidcOptions { Scopes = ["profile"] }.EffectiveScopes);
    }

    [Fact]
    public void AllowedGroupsRestrictAccessWhenSet()
    {
        Assert.True(new UiOidcOptions().Allows([]));

        var restricted = new UiOidcOptions { AllowedGroups = ["admins", "media"] };
        Assert.True(restricted.Allows(["users", "media"]));
        Assert.False(restricted.Allows(["users"]));
        Assert.False(restricted.Allows(["Admins"]));
    }

    [Fact]
    public void SignOutGoesToTheProviderLogoutWithAReturnAddressWhenConfigured()
    {
        var origin = new Uri("https://convy.example.com");

        Assert.Equal("/auth/signed-out", Configured().SignedOutRedirect(origin));
        Assert.Equal("https://auth.example.com/logout?rd=https%3A%2F%2Fconvy.example.com%2F",
            Configured(o => o.Oidc.LogoutUrl = "https://auth.example.com/logout").SignedOutRedirect(origin));
        Assert.Equal("https://auth.example.com/logout?x=1&rd=https%3A%2F%2Fconvy.example.com%2F",
            Configured(o => o.Oidc.LogoutUrl = "https://auth.example.com/logout?x=1").SignedOutRedirect(origin));
    }

    [Theory]
    [InlineData("POST", "/api/ui/sync", true)]
    [InlineData("POST", "/API/UI/jobs/j_1/cancel", true)]
    [InlineData("POST", "/auth/logout", true)]
    [InlineData("DELETE", "/api/ui", true)]
    [InlineData("GET", "/api/ui/status", false)]
    [InlineData("HEAD", "/auth/login", false)]
    [InlineData("POST", "/api/uix", false)]
    [InlineData("POST", "/sync", false)]
    [InlineData("POST", "/mcp", false)]
    public void StateChangingUiRequestsNeedTheUiHeader(string method, string path, bool needed)
    {
        Assert.Equal(needed, UiRequestRules.NeedsUiHeader(method, path));
    }
}

public sealed class DataBrowserServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ConvyFactory _convy;
    private readonly SettingsFactory _settings;
    private readonly DataBrowserService _service;

    public DataBrowserServiceTests()
    {
        _connection.Open();
        _convy = new ConvyFactory(_connection);
        _settings = new SettingsFactory(_connection);

        using (var db = _convy.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        // Both contexts share one file in production; the second one adds its tables here.
        using (var db = _settings.CreateDbContext())
        {
            db.GetService<IRelationalDatabaseCreator>().CreateTables();
        }

        _service = new DataBrowserService(_convy, _settings);
    }

    public void Dispose() => _connection.Dispose();

    private async Task Seed()
    {
        await using var db = _convy.CreateDbContext();
        for (var i = 1; i <= 3; i++)
        {
            db.SearchResults.Add(new SearchResultEntry
            {
                Id = $"r_{i}",
                SearchId = "s_1",
                SourceId = "prowlarr:1",
                Protocol = "torrent",
                Title = i == 2 ? "Evanescence 100%_Fallen" : $"Album {i}",
                ContentId = "https://prowlarr/download?apikey=SECRET",
                DedupKey = $"k{i}",
                SourcesJson = "[]",
                MatchedQueriesJson = "[]",
                AvailabilityJson = "{}",
                CreatedAt = 1_791_460_800,
                ExpiresAt = 1_791_464_400,
            });
        }

        db.FileListings.Add(new FileListingEntry
        {
            ResultId = "r_1",
            FilesJson = new string('x', 2500),
            TorrentFile = new byte[1234],
            CreatedAt = 1_791_460_800,
            ExpiresAt = 1_791_464_400,
        });
        await db.SaveChangesAsync();

        await using var settings = _settings.CreateDbContext();
        settings.UserSettings.Add(new UserSettingEntry { Key = "UserSettings:AutoSyncEnabled", Value = "true" });
        await settings.SaveChangesAsync();
    }

    [Fact]
    public async Task ListsTheTablesOfBothContextsWithRowCountsAndWithoutSecretColumns()
    {
        await Seed();

        var tables = await _service.GetTablesAsync(CancellationToken.None);

        Assert.Equal(
            ["DownloadStates", "FileEntries", "FileListings", "Jobs", "SearchResults", "SearchSessions", "UserSettings"],
            tables.Select(t => t.Name));
        Assert.Equal(3, tables.Single(t => t.Name == "SearchResults").Rows);
        Assert.Equal(1, tables.Single(t => t.Name == "UserSettings").Rows);

        var results = tables.Single(t => t.Name == "SearchResults").Columns;
        Assert.DoesNotContain(results, c => c.Name == "ContentId");
        Assert.Equal("unix_time", results.Single(c => c.Name == "ExpiresAt").Kind);
        Assert.Equal("number", results.Single(c => c.Name == "SizeBytes").Kind);
        Assert.Equal("blob", tables.Single(t => t.Name == "FileListings").Columns.Single(c => c.Name == "TorrentFile").Kind);
        Assert.Equal("time", tables.Single(t => t.Name == "Jobs").Columns.Single(c => c.Name == "CreatedAt").Kind);
        Assert.Equal("bool", tables.Single(t => t.Name == "DownloadStates").Columns.Single(c => c.Name == "IsDownloaded").Kind);
    }

    [Fact]
    public async Task ReadsNewestRowsFirstWithPaging()
    {
        await Seed();

        var page = await _service.ReadAsync("SearchResults", new DataQuery(null, null, true, 0, 2), CancellationToken.None);

        Assert.Equal(3, page.Total);
        var id = IndexOf(page, "Id");
        Assert.Equal(["r_3", "r_2"], page.Rows.Select(r => r[id]));
        Assert.DoesNotContain(page.Rows.SelectMany(r => r), v => v is string text && text.Contains("SECRET"));
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), page.Rows[0][IndexOf(page, "CreatedAt")]);

        var next = await _service.ReadAsync("SearchResults", new DataQuery(null, null, true, 2, 2), CancellationToken.None);
        Assert.Equal(["r_1"], next.Rows.Select(r => r[id]));
    }

    [Fact]
    public async Task SearchesTextColumnsLiterallyAndSortsByAColumn()
    {
        await Seed();

        var found = await _service.ReadAsync("SearchResults", new DataQuery("100%_f", null, true, 0, 50), CancellationToken.None);
        Assert.Equal(["r_2"], found.Rows.Select(r => r[IndexOf(found, "Id")]));

        var none = await _service.ReadAsync("SearchResults", new DataQuery("%", null, true, 0, 50), CancellationToken.None);
        Assert.Equal(1, none.Total);

        // The secret column is not searched either.
        var secret = await _service.ReadAsync("SearchResults", new DataQuery("SECRET", null, true, 0, 50), CancellationToken.None);
        Assert.Equal(0, secret.Total);

        var sorted = await _service.ReadAsync("SearchResults", new DataQuery(null, "Title", false, 0, 50), CancellationToken.None);
        Assert.Equal(["Album 1", "Album 3", "Evanescence 100%_Fallen"], sorted.Rows.Select(r => r[IndexOf(sorted, "Title")]));
    }

    [Fact]
    public async Task ShowsBlobsAsTheirSizeAndCutsLongText()
    {
        await Seed();

        var page = await _service.ReadAsync("FileListings", new DataQuery(null, null, true, 0, 50), CancellationToken.None);

        var row = Assert.Single(page.Rows);
        Assert.Equal(1234L, row[IndexOf(page, "TorrentFile")]);
        var files = Assert.IsType<string>(row[IndexOf(page, "FilesJson")]);
        Assert.Equal(2001, files.Length);
        Assert.EndsWith("…", files);
    }

    [Fact]
    public async Task ReadsTheSettingsTable()
    {
        await Seed();

        var page = await _service.ReadAsync("UserSettings", new DataQuery(null, null, true, 0, 50), CancellationToken.None);

        Assert.Equal(["UserSettings:AutoSyncEnabled", "true"], Assert.Single(page.Rows));
    }

    [Fact]
    public async Task RejectsUnknownTablesAndColumns()
    {
        await Assert.ThrowsAsync<ConvyRequestException>(() =>
            _service.ReadAsync("sqlite_master", new DataQuery(null, null, true, 0, 50), CancellationToken.None));
        await Assert.ThrowsAsync<ConvyRequestException>(() =>
            _service.ReadAsync("SearchResults", new DataQuery(null, "ContentId", true, 0, 50), CancellationToken.None));
        await Assert.ThrowsAsync<ConvyRequestException>(() =>
            _service.ReadAsync("Jobs", new DataQuery(null, "Id; DROP TABLE Jobs", true, 0, 50), CancellationToken.None));
    }

    private static int IndexOf(DataPage page, string column) =>
        page.Columns.Select((c, i) => (c, i)).Single(x => x.c.Name == column).i;

    private sealed class ConvyFactory(SqliteConnection connection) : IDbContextFactory<ConvyDbContext>
    {
        public ConvyDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ConvyDbContext>().UseSqlite(connection).Options);
    }

    private sealed class SettingsFactory(SqliteConnection connection) : IDbContextFactory<SettingsDbContext>
    {
        public SettingsDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<SettingsDbContext>().UseSqlite(connection).Options);
    }
}

public sealed class UiStatusServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CombinesSyncDownloadersSourcesStorageAndJobCounts()
    {
        var store = new EfJobStore(_db);
        await store.CreateAsync(Job("h1", JobStatus.Downloading), CancellationToken.None);
        await store.CreateAsync(Job("h2", JobStatus.Downloading), CancellationToken.None);
        await store.CreateAsync(Job("h3", JobStatus.Failed), CancellationToken.None);

        var qbittorrent = new FakeDownloader();
        var slskd = new FakeDownloader(DownloadProviders.Slskd, Protocol.Soulseek);
        var tracker = new SyncStatusTracker();
        tracker.CycleStarted(_time.Now.AddSeconds(-5));
        tracker.CycleFinished(_time.Now, null);
        tracker.DownloaderSynced(new DownloaderSyncResult(DownloadProviders.QBittorrent, _time.Now, false, 0, 0, "refused"));

        var storage = new StorageLayoutStatus();
        storage.Set(["/data/media is on another filesystem"], [], _time.Now);

        var registry = new SourceRegistry(
            [new StaticProvider(new FakeSource("prowlarr:1", "Rutracker"))], Media.Health(), _time, NullLogger<SourceRegistry>.Instance);
        var jobs = new JobService(
            new DownloaderResolver([qbittorrent]), new FakeRules(), store, new JobTransitions(store, new RecordingJobEvents()),
            new FakeFileSystem(), new StaticOptions<JobOptions>(new JobOptions()), _time, NullLogger<JobService>.Instance);

        var service = new UiStatusService(
            new FakeSyncControl(),
            tracker,
            new DownloaderResolver([qbittorrent, slskd]),
            new MediaCatalogService(Media.Catalog(Media.Categories()), registry, jobs),
            storage,
            store,
            new StaticOptions<McpOptions>(new McpOptions { ApiKey = "key" }),
            _time);

        var status = await service.GetAsync(CancellationToken.None);

        Assert.Equal(_time.Now, status.Sync.LastFinishedAt);
        Assert.True(status.Sync.AutoSync);
        Assert.Equal(["qbittorrent", "slskd"], status.Downloaders.Select(d => d.Provider));
        Assert.Equal((false, "refused"), (status.Downloaders[0].Ok, status.Downloaders[0].Error));
        Assert.Null(status.Downloaders[1].LastSyncAt);
        Assert.Equal("soulseek", status.Downloaders[1].Protocol);
        Assert.Equal("Rutracker", Assert.Single(status.Sources).Name);
        Assert.Equal(["/data/media is on another filesystem"], status.Storage.Problems);
        Assert.Equal(2, status.Jobs["downloading"]);
        Assert.Equal(1, status.Jobs["failed"]);
        Assert.Equal(0, status.Jobs["completed"]);
        Assert.True(status.McpEnabled);
    }

    private JobRecord Job(string itemRef, JobStatus status) => new()
    {
        Id = 0,
        Provider = DownloadProviders.QBittorrent,
        ItemRef = itemRef,
        Category = "music",
        Title = itemRef,
        Status = status,
        CreatedAt = _time.Now,
        UpdatedAt = _time.Now,
        LastProgressAt = _time.Now,
    };

    private sealed class FakeSyncControl : ISyncControlService
    {
        public SyncStatusDto GetStatus() => new() { AutoSyncEnabled = true, IntervalSeconds = 60 };

        public Task<SyncStatusDto> UpdateSettingsAsync(SyncSettingsUpdateDto dto, CancellationToken cancellationToken) =>
            Task.FromResult(GetStatus());

        public bool TryTriggerSync() => true;

        public void QueueSync()
        {
        }
    }
}
