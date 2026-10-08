namespace Convy.Services.Sync;

/// <summary>Outcome of the last sync of one downloader.</summary>
/// <param name="Provider">Downloader.</param>
/// <param name="At">When it finished.</param>
/// <param name="Ok">Whether the downloader could be read.</param>
/// <param name="Items">Items the downloader reported.</param>
/// <param name="Processed">Items that were processed (placed, retried or skipped) in that cycle.</param>
/// <param name="Error">Why it failed.</param>
public sealed record DownloaderSyncResult(string Provider, DateTimeOffset At, bool Ok, int Items, int Processed, string? Error);

/// <summary>The last sync cycle.</summary>
public sealed record SyncCycleResult(DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error);

/// <summary>
/// Remembers what the last sync cycles did, for the status page. In memory only: after a
/// restart it is empty until the first cycle.
/// </summary>
public sealed class SyncStatusTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DownloaderSyncResult> _downloaders = new(StringComparer.Ordinal);
    private SyncCycleResult? _lastCycle;

    public SyncCycleResult? LastCycle
    {
        get
        {
            lock (_gate) return _lastCycle;
        }
    }

    public IReadOnlyList<DownloaderSyncResult> Downloaders
    {
        get
        {
            lock (_gate) return _downloaders.Values.OrderBy(d => d.Provider, StringComparer.Ordinal).ToList();
        }
    }

    public void CycleStarted(DateTimeOffset at)
    {
        lock (_gate) _lastCycle = new SyncCycleResult(at, null, null);
    }

    public void CycleFinished(DateTimeOffset at, string? error)
    {
        lock (_gate) _lastCycle = (_lastCycle ?? new SyncCycleResult(at, null, null)) with { FinishedAt = at, Error = error };
    }

    public void DownloaderSynced(DownloaderSyncResult result)
    {
        lock (_gate) _downloaders[result.Provider] = result;
    }
}
