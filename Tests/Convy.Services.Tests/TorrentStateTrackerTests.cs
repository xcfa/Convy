using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Sync;
using Banned.Qbittorrent.Models.Torrent;
using Convy.Services.Tracking;
using Xunit;

namespace Convy.Services.Tests;

public class TorrentStateTrackerTests
{
    private const EnumTorrentState Downloaded = EnumTorrentState.StalledUpload;
    private const EnumTorrentState Downloading = EnumTorrentState.Downloading;

    private sealed class FakeStore : ITorrentStateStore
    {
        public Dictionary<string, TorrentStateSnapshot> Data { get; } = new();

        public Task<IReadOnlyDictionary<string, TorrentStateSnapshot>> LoadAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, TorrentStateSnapshot>>(
                new Dictionary<string, TorrentStateSnapshot>(Data));

        public Task UpsertAsync(IReadOnlyCollection<TorrentStateSnapshot> snapshots, CancellationToken ct)
        {
            foreach (var s in snapshots)
                Data[s.Hash] = s;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(IReadOnlyCollection<string> hashes, CancellationToken ct)
        {
            foreach (var h in hashes)
                Data.Remove(h);
            return Task.CompletedTask;
        }
    }

    private static MainData Sync(bool full, params (string hash, EnumTorrentState? state, long? size)[] torrents)
    {
        var dict = new Dictionary<string, TorrentInfo>();
        foreach (var (hash, state, size) in torrents)
            dict[hash] = new TorrentInfo { State = state, Size = size };

        return new MainData { Torrents = dict, FullUpdateEnabled = full };
    }

    private static MainData Removal(bool full, string[] removed, params (string hash, EnumTorrentState? state, long? size)[] torrents)
    {
        var data = Sync(full, torrents);
        data.TorrentsRemoved = removed;
        return data;
    }

    private static Task<IReadOnlyList<string>> Apply(TorrentStateTracker t, MainData m) =>
        t.ApplyAsync(m, CancellationToken.None);

    private static Task Confirm(TorrentStateTracker t, params string[] hashes) =>
        t.ConfirmProcessedAsync(hashes, CancellationToken.None);

    [Fact]
    public async Task FirstSeenDownloadedEmitsButBaselineOnlyAdvancesOnConfirm()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        var changes = await Apply(tracker, Sync(true, ("h1", Downloaded, 100)));

        Assert.Equal("h1", Assert.Single(changes));
        Assert.False(store.Data.ContainsKey("h1")); // not yet persisted

        await Confirm(tracker, "h1");
        Assert.True(store.Data["h1"].IsDownloaded);
    }

    [Fact]
    public async Task FirstSeenDownloadingDoesNotEmitButIsPersisted()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        var changes = await Apply(tracker, Sync(true, ("h1", Downloading, 100)));

        Assert.Empty(changes);
        Assert.False(store.Data["h1"].IsDownloaded); // non-actionable -> baseline advances
    }

    [Fact]
    public async Task ChangeIsReEmittedEveryCycleUntilConfirmed()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        // The torrent completes but we never confirm (e.g. linking keeps failing).
        Assert.Single(await Apply(tracker, Sync(true, ("h1", Downloaded, 100))));
        Assert.Single(await Apply(tracker, Sync(true, ("h1", Downloaded, 100))));
        Assert.Single(await Apply(tracker, Sync(true, ("h1", Downloaded, 100))));

        // Once confirmed, it stops being emitted.
        await Confirm(tracker, "h1");
        Assert.Empty(await Apply(tracker, Sync(true, ("h1", Downloaded, 100))));
    }

    [Fact]
    public async Task TransitionFromDownloadingToDownloadedEmitsOnceWhenConfirmed()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        Assert.Empty(await Apply(tracker, Sync(true, ("h1", Downloading, 100))));

        var changes = await Apply(tracker, Sync(false, ("h1", Downloaded, 100)));
        Assert.Equal("h1", Assert.Single(changes));
        await Confirm(tracker, "h1");

        Assert.Empty(await Apply(tracker, Sync(false, ("h1", Downloaded, 100))));
    }

    [Fact]
    public async Task PartialUpdateWithNullStateKeepsPreviousState()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        await Apply(tracker, Sync(true, ("h1", Downloaded, 100)));
        await Confirm(tracker, "h1");

        // Only a volatile field changed -> qBittorrent sends State=null, Size=null.
        var changes = await Apply(tracker, Sync(false, ("h1", null, null)));
        Assert.Empty(changes);
        Assert.True(store.Data["h1"].IsDownloaded);
    }

    [Fact]
    public async Task SizeChangeEmitsChange()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        await Apply(tracker, Sync(true, ("h1", Downloaded, 100)));
        await Confirm(tracker, "h1");

        var changes = await Apply(tracker, Sync(false, ("h1", null, 200)));
        Assert.Equal("h1", Assert.Single(changes));

        await Confirm(tracker, "h1");
        Assert.Equal(200, store.Data["h1"].Size);
    }

    [Fact]
    public async Task AfterRestartUnchangedTorrentIsNotReEmitted()
    {
        // A previous session already processed h1 as downloaded.
        var store = new FakeStore();
        store.Data["h1"] = new TorrentStateSnapshot("h1", IsDownloaded: true, Size: 100);

        var tracker = new TorrentStateTracker(store); // fresh instance == restart
        var changes = await Apply(tracker, Sync(true, ("h1", Downloaded, 100)));

        Assert.Empty(changes);
    }

    [Fact]
    public async Task AfterRestartTorrentCompletedWhileDownIsEmitted()
    {
        // Previous session left h1 still downloading.
        var store = new FakeStore();
        store.Data["h1"] = new TorrentStateSnapshot("h1", IsDownloaded: false, Size: 100);

        var tracker = new TorrentStateTracker(store);
        var changes = await Apply(tracker, Sync(true, ("h1", Downloaded, 100)));

        Assert.Equal("h1", Assert.Single(changes));
    }

    [Fact]
    public async Task MultipleTorrentsOnlyChangedOnesEmit()
    {
        var store = new FakeStore();
        store.Data["done"] = new TorrentStateSnapshot("done", IsDownloaded: true, Size: 10);
        store.Data["wip"] = new TorrentStateSnapshot("wip", IsDownloaded: false, Size: 20);

        var tracker = new TorrentStateTracker(store);
        var changes = await Apply(tracker, Sync(true,
            ("done", Downloaded, 10),     // unchanged
            ("wip", Downloaded, 20),      // completed -> emit
            ("new", Downloaded, 30)));    // brand new, downloaded -> emit

        Assert.Equal(2, changes.Count);
        Assert.Contains("wip", changes);
        Assert.Contains("new", changes);
        Assert.DoesNotContain("done", changes);
    }

    [Fact]
    public async Task ConfirmingUnknownHashIsANoOp()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        await Confirm(tracker, "ghost");
        Assert.False(store.Data.ContainsKey("ghost"));
    }

    [Fact]
    public async Task ExplicitlyRemovedTorrentIsPrunedFromStore()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        await Apply(tracker, Sync(true, ("h1", Downloaded, 100)));
        await Confirm(tracker, "h1");
        Assert.True(store.Data.ContainsKey("h1"));

        // A later partial update reports h1 as removed in qBittorrent.
        await Apply(tracker, Removal(full: false, removed: ["h1"]));

        Assert.False(store.Data.ContainsKey("h1"));
    }

    [Fact]
    public async Task FullSyncPrunesTorrentsAbsentFromIt()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        await Apply(tracker, Sync(true, ("h1", Downloaded, 100), ("h2", Downloaded, 200)));
        await Confirm(tracker, "h1", "h2");

        // h2 was removed while the app was down: it is simply absent from the next full sync.
        await Apply(tracker, Sync(true, ("h1", Downloaded, 100)));

        Assert.True(store.Data.ContainsKey("h1"));
        Assert.False(store.Data.ContainsKey("h2"));
    }

    [Fact]
    public async Task SizeOnlyPartialKeepsDownloadedFlagAfterRestart()
    {
        // h1 was downloaded in a previous session; observed is seeded from the store.
        var store = new FakeStore();
        store.Data["h1"] = new TorrentStateSnapshot("h1", IsDownloaded: true, Size: 100);

        var tracker = new TorrentStateTracker(store);
        Assert.Empty(await Apply(tracker, Sync(true, ("h1", Downloaded, 100))));

        // A later partial carries only Size (State=null). The download flag is kept from
        // the observed baseline, so this is a size change, not a fresh "became downloaded".
        var changes = await Apply(tracker, Sync(false, ("h1", null, 200)));
        Assert.Equal("h1", Assert.Single(changes));
    }

    [Fact]
    public async Task PrunedTorrentIsTreatedAsNewIfItReappears()
    {
        var store = new FakeStore();
        var tracker = new TorrentStateTracker(store);

        await Apply(tracker, Sync(true, ("h1", Downloaded, 100)));
        await Confirm(tracker, "h1");

        await Apply(tracker, Removal(full: false, removed: ["h1"]));

        // Re-added later -> forgotten, so it emits again.
        var changes = await Apply(tracker, Sync(false, ("h1", Downloaded, 100)));
        Assert.Equal("h1", Assert.Single(changes));
    }
}
