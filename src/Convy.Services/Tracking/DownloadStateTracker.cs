using Convy.Services.Downloads;

namespace Convy.Services.Tracking
{
    /// <summary>
    /// Tracks each item's download-completion and size. Keeps three views, all keyed by
    /// (provider, item reference):
    /// <list type="bullet">
    /// <item><b>observed</b> — the latest state seen from the downloader for every known
    /// item (in-memory; seeded from the persisted baseline on startup).</item>
    /// <item><b>processed</b> — the last state we acted upon for items that matched a rule
    /// and were linked; persisted via <see cref="IDownloadStateStore"/> so they are not
    /// reprocessed across restarts.</item>
    /// <item><b>skipped</b> — items that matched no rule. In-memory only and never
    /// persisted, so after a restart they are re-evaluated against the current rules
    /// (which may have changed while the app was down).</item>
    /// </list>
    /// An item is reported as changed when it is downloaded and its tracked state diverges
    /// from processed, unless it is currently skipped at that same state. The processed
    /// baseline is only advanced once the consumer confirms a successful link
    /// (<see cref="ConfirmProcessedAsync"/>); until then the change is re-emitted so a
    /// transient failure is retried.
    /// </summary>
    public sealed class DownloadStateTracker : IDownloadStateTracker
    {
        private readonly IDownloadStateStore _store;
        private readonly SemaphoreSlim _sync = new(1, 1);

        private readonly Dictionary<Key, Snapshot> _observed = new();
        private readonly Dictionary<Key, Snapshot> _skipped = new();

        private Dictionary<Key, Snapshot>? _processed;

        public DownloadStateTracker(IDownloadStateStore store) => _store = store;

        public async Task<IReadOnlyList<string>> ApplyAsync(
            string provider, IReadOnlyCollection<DownloadItem> items, CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

                var present = new HashSet<Key>();
                foreach (var item in items)
                {
                    var key = new Key(provider, item.ItemRef);
                    present.Add(key);
                    _observed[key] = new Snapshot(item.IsDownloaded, item.Size);
                }

                // The list is authoritative: anything of this provider we track but that is
                // absent from it is gone (removed, possibly while the app was down). This keeps
                // the in-memory and persisted state from growing without bound.
                var removed = _observed.Keys
                    .Where(k => k.Provider == provider && !present.Contains(k))
                    .ToList();

                foreach (var key in removed)
                {
                    _observed.Remove(key);
                    _processed!.Remove(key);
                    _skipped.Remove(key);
                }

                var changes = new List<string>();
                var dirty = new List<DownloadStateSnapshot>();

                foreach (var key in present)
                {
                    var observed = _observed[key];

                    // Suppress a no-rule item while it hasn't changed since we skipped it.
                    if (_skipped.TryGetValue(key, out var skippedAt) && skippedAt == observed)
                    {
                        continue;
                    }

                    var hadProcessed = _processed!.TryGetValue(key, out var processed);
                    var changed = !hadProcessed || processed != observed;

                    if (observed.IsDownloaded && changed)
                    {
                        changes.Add(key.ItemRef);
                    }
                    else if (changed)
                    {
                        // Non-actionable change (e.g. a regression to a non-downloaded
                        // state): advance the baseline so a later completion is detected.
                        _processed[key] = observed;
                        dirty.Add(observed.ToSnapshot(key));
                    }
                }

                await _store.UpsertAsync(dirty, cancellationToken).ConfigureAwait(false);

                if (removed.Count > 0)
                {
                    await _store.RemoveAsync(provider, removed.Select(k => k.ItemRef).ToList(), cancellationToken)
                        .ConfigureAwait(false);
                }

                return changes;
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task ConfirmProcessedAsync(string provider, IEnumerable<string> itemRefs, CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

                var dirty = new List<DownloadStateSnapshot>();

                foreach (var itemRef in itemRefs)
                {
                    var key = new Key(provider, itemRef);

                    // A confirmed item matched a rule (or a job), so it is no longer skipped.
                    _skipped.Remove(key);

                    if (!_observed.TryGetValue(key, out var observed))
                    {
                        continue;
                    }

                    if (!_processed!.TryGetValue(key, out var processed) || processed != observed)
                    {
                        _processed[key] = observed;
                        dirty.Add(observed.ToSnapshot(key));
                    }
                }

                await _store.UpsertAsync(dirty, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task MarkSkippedAsync(string provider, IEnumerable<string> itemRefs, CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

                foreach (var itemRef in itemRefs)
                {
                    var key = new Key(provider, itemRef);
                    if (_observed.TryGetValue(key, out var observed))
                    {
                        _skipped[key] = observed;
                    }
                }
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task ClearSkippedAsync(CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                _skipped.Clear();
            }
            finally
            {
                _sync.Release();
            }
        }

        private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (_processed is not null)
            {
                return;
            }

            var persisted = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            _processed = persisted.ToDictionary(
                s => new Key(s.Provider, s.ItemRef),
                s => new Snapshot(s.IsDownloaded, s.Size));

            // Seed observed with the persisted baseline so nothing is spuriously re-emitted
            // and items removed while the app was down are pruned on the first apply.
            foreach (var (key, snapshot) in _processed)
            {
                _observed[key] = snapshot;
            }
        }

        private readonly record struct Key(string Provider, string ItemRef);

        private readonly record struct Snapshot(bool IsDownloaded, long? Size)
        {
            public DownloadStateSnapshot ToSnapshot(Key key) => new(key.Provider, key.ItemRef, IsDownloaded, Size);
        }
    }
}
