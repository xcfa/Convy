using Convy.Services.Downloads;
using Convy.Services.Tracking;
using Xunit;

namespace Convy.Services.Tests;

public class DownloadStateTrackerTests
{
    private const string Qbt = DownloadProviders.QBittorrent;
    private const string Slskd = DownloadProviders.Slskd;

    private sealed class FakeStore : IDownloadStateStore
    {
        public Dictionary<(string Provider, string ItemRef), DownloadStateSnapshot> Data { get; } = new();

        public Task<IReadOnlyList<DownloadStateSnapshot>> LoadAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DownloadStateSnapshot>>(Data.Values.ToList());

        public Task UpsertAsync(IReadOnlyCollection<DownloadStateSnapshot> snapshots, CancellationToken ct)
        {
            foreach (var s in snapshots)
                Data[(s.Provider, s.ItemRef)] = s;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string provider, IReadOnlyCollection<string> itemRefs, CancellationToken ct)
        {
            foreach (var r in itemRefs)
                Data.Remove((provider, r));
            return Task.CompletedTask;
        }

        public void Seed(string itemRef, bool downloaded, long size, string provider = Qbt) =>
            Data[(provider, itemRef)] = new DownloadStateSnapshot(provider, itemRef, downloaded, size);

        public bool Has(string itemRef, string provider = Qbt) => Data.ContainsKey((provider, itemRef));

        public DownloadStateSnapshot Get(string itemRef, string provider = Qbt) => Data[(provider, itemRef)];
    }

    private static DownloadItem Item(string itemRef, bool downloaded, long? size, string provider = Qbt) => new()
    {
        Provider = provider,
        ItemRef = itemRef,
        Name = itemRef,
        SavePath = "/data/downloads",
        State = downloaded ? DownloadState.Completed : DownloadState.Downloading,
        Size = size,
    };

    private static Task<IReadOnlyList<string>> Apply(DownloadStateTracker t, params DownloadItem[] items) =>
        t.ApplyAsync(Qbt, items, CancellationToken.None);

    private static Task Confirm(DownloadStateTracker t, params string[] refs) =>
        t.ConfirmProcessedAsync(Qbt, refs, CancellationToken.None);

    [Fact]
    public async Task FirstSeenDownloadedEmitsButBaselineOnlyAdvancesOnConfirm()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        var changes = await Apply(tracker, Item("h1", true, 100));

        Assert.Equal("h1", Assert.Single(changes));
        Assert.False(store.Has("h1")); // not yet persisted

        await Confirm(tracker, "h1");
        Assert.True(store.Get("h1").IsDownloaded);
    }

    [Fact]
    public async Task FirstSeenDownloadingDoesNotEmitButIsPersisted()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        var changes = await Apply(tracker, Item("h1", false, 100));

        Assert.Empty(changes);
        Assert.False(store.Get("h1").IsDownloaded); // non-actionable -> baseline advances
    }

    [Fact]
    public async Task ChangeIsReEmittedEveryCycleUntilConfirmed()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        // The item completes but we never confirm (e.g. linking keeps failing).
        Assert.Single(await Apply(tracker, Item("h1", true, 100)));
        Assert.Single(await Apply(tracker, Item("h1", true, 100)));
        Assert.Single(await Apply(tracker, Item("h1", true, 100)));

        // Once confirmed, it stops being emitted.
        await Confirm(tracker, "h1");
        Assert.Empty(await Apply(tracker, Item("h1", true, 100)));
    }

    [Fact]
    public async Task TransitionFromDownloadingToDownloadedEmitsOnceWhenConfirmed()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        Assert.Empty(await Apply(tracker, Item("h1", false, 100)));

        var changes = await Apply(tracker, Item("h1", true, 100));
        Assert.Equal("h1", Assert.Single(changes));
        await Confirm(tracker, "h1");

        Assert.Empty(await Apply(tracker, Item("h1", true, 100)));
    }

    [Fact]
    public async Task SizeChangeEmitsChange()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        await Apply(tracker, Item("h1", true, 100));
        await Confirm(tracker, "h1");

        var changes = await Apply(tracker, Item("h1", true, 200));
        Assert.Equal("h1", Assert.Single(changes));

        await Confirm(tracker, "h1");
        Assert.Equal(200, store.Get("h1").Size);
    }

    [Fact]
    public async Task AfterRestartUnchangedItemIsNotReEmitted()
    {
        // A previous session already processed h1 as downloaded.
        var store = new FakeStore();
        store.Seed("h1", downloaded: true, size: 100);

        var tracker = new DownloadStateTracker(store); // fresh instance == restart
        Assert.Empty(await Apply(tracker, Item("h1", true, 100)));
    }

    [Fact]
    public async Task AfterRestartItemCompletedWhileDownIsEmitted()
    {
        // Previous session left h1 still downloading.
        var store = new FakeStore();
        store.Seed("h1", downloaded: false, size: 100);

        var tracker = new DownloadStateTracker(store);
        var changes = await Apply(tracker, Item("h1", true, 100));

        Assert.Equal("h1", Assert.Single(changes));
    }

    [Fact]
    public async Task MultipleItemsOnlyChangedOnesEmit()
    {
        var store = new FakeStore();
        store.Seed("done", downloaded: true, size: 10);
        store.Seed("wip", downloaded: false, size: 20);

        var tracker = new DownloadStateTracker(store);
        var changes = await Apply(tracker,
            Item("done", true, 10),     // unchanged
            Item("wip", true, 20),      // completed -> emit
            Item("new", true, 30));     // brand new, downloaded -> emit

        Assert.Equal(2, changes.Count);
        Assert.Contains("wip", changes);
        Assert.Contains("new", changes);
        Assert.DoesNotContain("done", changes);
    }

    [Fact]
    public async Task ConfirmingUnknownItemIsANoOp()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        await Confirm(tracker, "ghost");
        Assert.False(store.Has("ghost"));
    }

    [Fact]
    public async Task ItemAbsentFromListIsPrunedFromStore()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        await Apply(tracker, Item("h1", true, 100), Item("h2", true, 200));
        await Confirm(tracker, "h1", "h2");

        // h2 was removed (possibly while the app was down): it is simply absent.
        await Apply(tracker, Item("h1", true, 100));

        Assert.True(store.Has("h1"));
        Assert.False(store.Has("h2"));
    }

    [Fact]
    public async Task ItemRemovedWhileDownIsPrunedOnFirstApply()
    {
        var store = new FakeStore();
        store.Seed("gone", downloaded: true, size: 1);

        var tracker = new DownloadStateTracker(store);
        await Apply(tracker);

        Assert.False(store.Has("gone"));
    }

    [Fact]
    public async Task PrunedItemIsTreatedAsNewIfItReappears()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        await Apply(tracker, Item("h1", true, 100));
        await Confirm(tracker, "h1");

        await Apply(tracker);

        // Re-added later -> forgotten, so it emits again.
        var changes = await Apply(tracker, Item("h1", true, 100));
        Assert.Equal("h1", Assert.Single(changes));
    }

    [Fact]
    public async Task ProvidersAreTrackedIndependently()
    {
        var store = new FakeStore();
        store.Seed("album", downloaded: true, size: 5, provider: Slskd);

        var tracker = new DownloadStateTracker(store);

        // A qBittorrent list never prunes slskd items, even when it is empty.
        await Apply(tracker);
        Assert.True(store.Has("album", Slskd));

        // The same reference under another provider is a different item.
        Assert.Equal("album", Assert.Single(await Apply(tracker, Item("album", true, 5))));
        Assert.Empty(await tracker.ApplyAsync(Slskd, [Item("album", true, 5, Slskd)], CancellationToken.None));
    }

    [Fact]
    public async Task SkippedItemIsSuppressedUntilCleared()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        // First sight: emitted.
        Assert.Equal("h1", Assert.Single(await Apply(tracker, Item("h1", true, 100))));

        // Marked skipped (no rule matched) -> not re-emitted while unchanged.
        await tracker.MarkSkippedAsync(Qbt, ["h1"], CancellationToken.None);
        Assert.Empty(await Apply(tracker, Item("h1", true, 100)));

        // Rules change -> clear skipped -> re-emitted for re-evaluation.
        await tracker.ClearSkippedAsync(CancellationToken.None);
        Assert.Equal("h1", Assert.Single(await Apply(tracker, Item("h1", true, 100))));
    }

    [Fact]
    public async Task SkippedItemIsReEmittedWhenItChanges()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        Assert.Single(await Apply(tracker, Item("h1", true, 100)));
        await tracker.MarkSkippedAsync(Qbt, ["h1"], CancellationToken.None);

        // Unchanged -> suppressed.
        Assert.Empty(await Apply(tracker, Item("h1", true, 100)));

        // Size changes -> re-emitted.
        Assert.Equal("h1", Assert.Single(await Apply(tracker, Item("h1", true, 200))));
    }

    [Fact]
    public async Task SkippedItemIsNotPersistedSoReEvaluatedAfterRestart()
    {
        var store = new FakeStore();
        var tracker = new DownloadStateTracker(store);

        Assert.Single(await Apply(tracker, Item("h1", true, 100)));
        await tracker.MarkSkippedAsync(Qbt, ["h1"], CancellationToken.None);
        Assert.Empty(await Apply(tracker, Item("h1", true, 100)));

        // A skipped item is never persisted, so it stays out of the store...
        Assert.False(store.Has("h1"));

        // ...and a fresh instance (restart) re-emits it for re-evaluation.
        var restarted = new DownloadStateTracker(store);
        Assert.Equal("h1", Assert.Single(await Apply(restarted, Item("h1", true, 100))));
    }
}
