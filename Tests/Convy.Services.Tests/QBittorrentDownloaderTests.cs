using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Sync;
using Banned.Qbittorrent.Models.Torrent;
using Convy.PathExpressions.Properties;
using Convy.Services.Downloaders.QBittorrent;
using Convy.Services.Downloads;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class QBittorrentDownloaderTests
{
    internal sealed class FakeApi : IQBittorrentApi
    {
        public Queue<MainData> MainData { get; } = new();
        public List<int> RequestedRids { get; } = [];
        public Dictionary<string, TorrentInfo> Torrents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<TorrentFileInfo>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Kind, string? Category, bool Stopped)> Added { get; } = [];
        public List<(string Hash, List<int> Indexes, EnumTorrentFilePriority Priority)> Priorities { get; } = [];
        public List<string> Started { get; } = [];
        public List<string> Stopped { get; } = [];
        public List<string> Removed { get; } = [];
        public Exception? PriorityFailure { get; set; }

        /// <summary>What appears in qBittorrent after an add call.</summary>
        public Action? OnAdd { get; set; }

        public Task<MainData> GetMainDataAsync(int rid, CancellationToken ct)
        {
            RequestedRids.Add(rid);
            return MainData.Count > 0
                ? Task.FromResult(MainData.Dequeue())
                : Task.FromException<MainData>(new HttpRequestException("down"));
        }

        public Task<TorrentInfo?> GetTorrentInfoAsync(string hash, CancellationToken ct) =>
            Task.FromResult(Torrents.GetValueOrDefault(hash));

        public Task<IReadOnlyList<TorrentFileInfo>?> GetTorrentFilesAsync(string hash, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TorrentFileInfo>?>(Files.GetValueOrDefault(hash));

        public Task AddTorrentFileAsync(byte[] torrentFile, string? category, bool stopped, CancellationToken ct)
        {
            Added.Add(("file", category, stopped));
            OnAdd?.Invoke();
            return Task.CompletedTask;
        }

        public Task AddMagnetAsync(string magnet, string? category, bool stopped, CancellationToken ct)
        {
            Added.Add(("magnet", category, stopped));
            OnAdd?.Invoke();
            return Task.CompletedTask;
        }

        public Task SetFilesPriorityAsync(string hash, IReadOnlyList<int> fileIndexes, EnumTorrentFilePriority priority, CancellationToken ct)
        {
            if (PriorityFailure is not null)
                return Task.FromException(PriorityFailure);

            Priorities.Add((hash, fileIndexes.ToList(), priority));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string hash, CancellationToken ct)
        {
            Removed.Add(hash);
            Torrents.Remove(hash);
            return Task.CompletedTask;
        }

        public Task StopAsync(string hash, CancellationToken ct)
        {
            Stopped.Add(hash);
            return Task.CompletedTask;
        }

        public Task StartAsync(string hash, CancellationToken ct)
        {
            Started.Add(hash);
            return Task.CompletedTask;
        }

        public Task<string> GetDefaultSavePathAsync(CancellationToken ct) => Task.FromResult("/data/downloads");

        public Task<IReadOnlyList<TorrentCategory>> GetCategoriesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TorrentCategory>>([]);
    }

    private static QBittorrentDownloader Create(FakeApi api) =>
        new(api, TimeProvider.System, NullLogger<QBittorrentDownloader>.Instance);

    private static MainData SyncData(int rid, bool full, params (string Hash, TorrentInfo Info)[] torrents) => new()
    {
        Rid = rid,
        FullUpdateEnabled = full,
        Torrents = torrents.ToDictionary(t => t.Hash, t => t.Info),
    };

    [Fact]
    public async Task PartialUpdatesAreMergedOntoTheLastFullState()
    {
        var api = new FakeApi();
        api.MainData.Enqueue(SyncData(1, true, ("h1", new TorrentInfo
        {
            Name = "Movie", State = EnumTorrentState.Downloading, Size = 100, SavePath = "/dl", Category = "Movies",
        })));
        // Only the state changed; every other field is absent (null).
        api.MainData.Enqueue(SyncData(2, false, ("h1", new TorrentInfo { State = EnumTorrentState.StalledUpload })));
        // Only a volatile field changed.
        api.MainData.Enqueue(SyncData(3, false, ("h1", new TorrentInfo { Ratio = 0.5f })));

        var downloader = Create(api);

        var first = Assert.Single(await downloader.GetItemsAsync(CancellationToken.None));
        Assert.Equal(DownloadState.Downloading, first.State);

        var second = Assert.Single(await downloader.GetItemsAsync(CancellationToken.None));
        Assert.Equal(DownloadState.Completed, second.State);
        Assert.Equal(100, second.Size);
        Assert.Equal("Movies", second.Properties["Category"]);
        Assert.Equal("h1", second.ItemRef);

        var third = Assert.Single(await downloader.GetItemsAsync(CancellationToken.None));
        Assert.Equal(DownloadState.Completed, third.State);
        Assert.Equal(0.5, (double)third.Properties["Ratio"]!, 3);

        Assert.Equal([0, 1, 2], api.RequestedRids);
    }

    [Fact]
    public async Task RemovedAndFullUpdatesDropTorrents()
    {
        var api = new FakeApi();
        api.MainData.Enqueue(SyncData(1, true,
            ("h1", new TorrentInfo { State = EnumTorrentState.Uploading }),
            ("h2", new TorrentInfo { State = EnumTorrentState.Uploading }),
            ("h3", new TorrentInfo { State = EnumTorrentState.Uploading })));
        var removal = SyncData(2, false);
        removal.TorrentsRemoved = ["h1"];
        api.MainData.Enqueue(removal);
        api.MainData.Enqueue(SyncData(3, true, ("h2", new TorrentInfo { State = EnumTorrentState.Uploading })));

        var downloader = Create(api);

        Assert.Equal(3, (await downloader.GetItemsAsync(CancellationToken.None)).Count);
        Assert.Equal(["h2", "h3"], (await downloader.GetItemsAsync(CancellationToken.None)).Select(i => i.ItemRef).Order());
        Assert.Equal("h2", Assert.Single(await downloader.GetItemsAsync(CancellationToken.None)).ItemRef);
    }

    [Fact]
    public async Task FailedSyncRestartsWithAFullUpdate()
    {
        var api = new FakeApi();
        api.MainData.Enqueue(SyncData(7, true, ("h1", new TorrentInfo { State = EnumTorrentState.Uploading })));
        var downloader = Create(api);

        await downloader.GetItemsAsync(CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.GetItemsAsync(CancellationToken.None));

        api.MainData.Enqueue(SyncData(1, true));
        await downloader.GetItemsAsync(CancellationToken.None);

        Assert.Equal([0, 7, 0], api.RequestedRids);
    }

    [Theory]
    [InlineData(EnumTorrentState.StalledUpload, DownloadState.Completed)]
    [InlineData(EnumTorrentState.StoppedUpload, DownloadState.Completed)]
    [InlineData(EnumTorrentState.Downloading, DownloadState.Downloading)]
    [InlineData(EnumTorrentState.StalledDownload, DownloadState.Downloading)]
    [InlineData(EnumTorrentState.MetaDownload, DownloadState.Queued)]
    [InlineData(EnumTorrentState.QueuedDownload, DownloadState.Queued)]
    [InlineData(EnumTorrentState.StoppedDownload, DownloadState.Paused)]
    [InlineData(EnumTorrentState.Error, DownloadState.Errored)]
    [InlineData(EnumTorrentState.MissingFiles, DownloadState.Errored)]
    [InlineData(EnumTorrentState.Moving, DownloadState.Unknown)]
    public void MapsTorrentStates(EnumTorrentState state, DownloadState expected) =>
        Assert.Equal(expected, QBittorrentDownloader.MapState(state));

    [Fact]
    public async Task GetItemReturnsFilesWithSelection()
    {
        var api = new FakeApi();
        api.Torrents["h1"] = new TorrentInfo { Name = "Show", SavePath = "/dl", State = EnumTorrentState.Uploading };
        api.Files["h1"] =
        [
            new TorrentFileInfo { Index = 0, Name = "Show/a.mkv", Size = 10, Progress = 1, Priority = EnumTorrentFilePriority.Normal },
            new TorrentFileInfo { Index = 1, Name = "Show/b.mkv", Size = 20, Progress = 0, Priority = EnumTorrentFilePriority.DoNotDownload },
        ];

        var item = await Create(api).GetItemAsync("h1", CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal("/dl", item.SavePath);
        Assert.True(item.Files[0].IsComplete);
        Assert.False(item.Files[1].Selected);
        Assert.Null(await Create(api).GetItemAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task AddWithSelectionAddsStoppedDeselectsAndStarts()
    {
        var api = new FakeApi();
        api.OnAdd = () =>
        {
            api.Torrents["abc"] = new TorrentInfo { Hash = "abc", State = EnumTorrentState.StoppedDownload };
            api.Files["abc"] =
            [
                new TorrentFileInfo { Index = 0, Name = "Show/Season 01/e1.mkv" },
                new TorrentFileInfo { Index = 1, Name = "Show/Season 02/e1.mkv" },
                new TorrentFileInfo { Index = 2, Name = "Show/Season 02/sample.mkv" },
            ];
        };

        var hash = await Create(api).AddAsync(
            new TorrentPayload("ABC", "magnet:?xt=urn:btih:abc", [1, 2, 3]),
            new FileSelection(["Season 02/e1.mkv"]),
            new AddOptions("Series"),
            CancellationToken.None);

        Assert.Equal("abc", hash);
        Assert.Equal(("file", "Series", true), Assert.Single(api.Added));
        var priority = Assert.Single(api.Priorities);
        Assert.Equal([0, 2], priority.Indexes);
        Assert.Equal(EnumTorrentFilePriority.DoNotDownload, priority.Priority);
        Assert.Equal("abc", Assert.Single(api.Started));
    }

    private static void ShowWithTwoSeasons(FakeApi api) => api.OnAdd = () =>
    {
        api.Torrents["abc"] = new TorrentInfo { Hash = "abc", State = EnumTorrentState.StoppedDownload };
        api.Files["abc"] =
        [
            new TorrentFileInfo { Index = 0, Name = "Show/Season 01/e1.mkv" },
            new TorrentFileInfo { Index = 1, Name = "Show/Season 02/e1.mkv" },
        ];
    };

    [Fact]
    public async Task FailedSelectionRemovesTheHalfAddedTorrent()
    {
        var api = new FakeApi { PriorityFailure = new HttpRequestException("409 metadata not ready") };
        ShowWithTwoSeasons(api);

        await Assert.ThrowsAsync<HttpRequestException>(() => Create(api).AddAsync(
            new TorrentPayload("abc", null, [1]), new FileSelection(["Season 02/e1.mkv"]), new AddOptions(null), CancellationToken.None));

        Assert.Equal("abc", Assert.Single(api.Removed));
        Assert.Empty(api.Started);
    }

    [Fact]
    public async Task SelectionThatDoesNotMatchTheTorrentIsAnError()
    {
        var api = new FakeApi();
        ShowWithTwoSeasons(api);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(api).AddAsync(
            new TorrentPayload("abc", null, [1]), new FileSelection(["Season 03/e1.mkv"]), new AddOptions(null), CancellationToken.None));

        Assert.Contains("0 of the 1", ex.Message);
        Assert.Empty(api.Priorities);
        Assert.Equal("abc", Assert.Single(api.Removed));
    }

    [Fact]
    public void ItemRefIsTheLowerCaseInfoHash() =>
        Assert.Equal("abc", Create(new FakeApi()).GetItemRef(new TorrentPayload("ABC", "magnet:?xt=urn:btih:ABC", null)));

    [Fact]
    public async Task AddWithoutSelectionPrefersMagnetOnlyWhenNoFile()
    {
        var api = new FakeApi();
        api.OnAdd = () => api.Torrents["abc"] = new TorrentInfo { Hash = "abc" };

        await Create(api).AddAsync(
            new TorrentPayload("abc", "magnet:?xt=urn:btih:abc", null),
            FileSelection.All, new AddOptions("Movies"), CancellationToken.None);

        Assert.Equal(("magnet", "Movies", false), Assert.Single(api.Added));
        Assert.Empty(api.Priorities);
        Assert.Empty(api.Started);
    }

    [Fact]
    public async Task AddReusesTorrentAlreadyInClient()
    {
        var api = new FakeApi();
        api.Torrents["abc"] = new TorrentInfo { Hash = "abc", State = EnumTorrentState.Uploading };

        var hash = await Create(api).AddAsync(
            new TorrentPayload("abc", "magnet:?xt=urn:btih:abc", null),
            new FileSelection(["a.mkv"]), new AddOptions("Movies"), CancellationToken.None);

        Assert.Equal("abc", hash);
        Assert.Empty(api.Added);
        Assert.Empty(api.Priorities);
    }

    [Fact]
    public async Task ReusedTorrentGetsTheWantedFilesSwitchedOnButNothingSwitchedOff()
    {
        var api = new FakeApi();
        api.Torrents["abc"] = new TorrentInfo { Hash = "abc", State = EnumTorrentState.Uploading };
        api.Files["abc"] =
        [
            new TorrentFileInfo { Index = 0, Name = "Album/01.flac", Priority = EnumTorrentFilePriority.Normal },
            new TorrentFileInfo { Index = 1, Name = "Album/02.flac", Priority = EnumTorrentFilePriority.DoNotDownload },
            new TorrentFileInfo { Index = 2, Name = "Album/cover.jpg", Priority = EnumTorrentFilePriority.DoNotDownload },
        ];

        await Create(api).AddAsync(
            new TorrentPayload("abc", null, [1]), new FileSelection(["02.flac"]), new AddOptions(null), CancellationToken.None);

        var priority = Assert.Single(api.Priorities);
        Assert.Equal([1], priority.Indexes);
        Assert.Equal(EnumTorrentFilePriority.Normal, priority.Priority);
        Assert.Equal("abc", Assert.Single(api.Started));
        Assert.Empty(api.Added);
    }

    [Fact]
    public async Task SelectionWithoutMetadataIsRejected()
    {
        var api = new FakeApi();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(api).AddAsync(
            new TorrentPayload("abc", "magnet:?xt=urn:btih:abc", null),
            new FileSelection(["a.mkv"]), new AddOptions(null), CancellationToken.None));

        Assert.Empty(api.Added);
    }

    [Fact]
    public async Task CancelStopsTheTorrent()
    {
        var api = new FakeApi();
        await Create(api).CancelAsync("abc", CancellationToken.None);
        Assert.Equal("abc", Assert.Single(api.Stopped));
    }

    [Fact]
    public void PublishedPropertiesAreKnownToTheRuleLanguageWithMatchingKinds()
    {
        var properties = TorrentInfoProperties.ToProperties(new TorrentInfo
        {
            Size = 1, Name = "n", AutoTmmEnabled = true, TagList = ["t"], SeedingTime = TimeSpan.FromSeconds(5),
        });

        foreach (var name in TorrentInfoProperties.Names)
        {
            var rule = RuleProperties.Find(name);
            Assert.True(rule is not null, $"'{name}' is not a rule property");
            Assert.Equal(name, rule.Name);
        }

        Assert.IsType<double>(properties["Size"]);
        Assert.IsType<double>(properties["SeedingTime"]);
        Assert.IsType<string>(properties["Name"]);
        Assert.IsType<bool>(properties["AutoTmmEnabled"]);
        Assert.IsAssignableFrom<IEnumerable<string>>(properties["Tags"]);
    }
}
