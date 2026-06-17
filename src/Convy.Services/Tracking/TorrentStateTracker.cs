using Banned.Qbittorrent.Models.Sync;

namespace Convy.Services.Tracking
{
    /// <summary>
    /// Tracks each torrent's download-completion and size. Keeps three views:
    /// <list type="bullet">
    /// <item><b>observed</b> — the latest state seen from qBittorrent for every known
    /// torrent (in-memory; seeded from the persisted baseline on startup). The merge
    /// accumulator for partial updates.</item>
    /// <item><b>processed</b> — the last state we acted upon for torrents that matched a
    /// rule and were linked; persisted via <see cref="ITorrentStateStore"/> so they are
    /// not reprocessed across restarts.</item>
    /// <item><b>skipped</b> — torrents that matched no rule. In-memory only and never
    /// persisted, so after a restart they are re-evaluated against the current rules
    /// (which may have changed while the app was down).</item>
    /// </list>
    /// A torrent is reported as changed when it is downloaded and its tracked state
    /// diverges from processed, unless it is currently skipped at that same state. The
    /// processed baseline is only advanced once the consumer confirms a successful link
    /// (<see cref="ConfirmProcessedAsync"/>); until then the change is re-emitted so a
    /// transient failure is retried.
    /// </summary>
    public sealed class TorrentStateTracker : ITorrentStateTracker
    {
        private readonly ITorrentStateStore _store;
        private readonly SemaphoreSlim _sync = new(1, 1);

        private readonly Dictionary<string, Snapshot> _observed = new();
        private readonly Dictionary<string, Snapshot> _skipped = new();

        private Dictionary<string, Snapshot>? _processed;

        public TorrentStateTracker(ITorrentStateStore store) => _store = store;

        public async Task<IReadOnlyList<string>> ApplyAsync(MainData mainData, CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken);

            try
            {
                await EnsureLoadedAsync(cancellationToken);

                MergeObserved(mainData);

                var removed = CollectRemoved(mainData);
                foreach (var hash in removed)
                {
                    _observed.Remove(hash);
                    _processed!.Remove(hash);
                    _skipped.Remove(hash);
                }

                var changes = new List<string>();
                var dirty = new List<TorrentStateSnapshot>();

                foreach (var (hash, observed) in _observed)
                {
                    // Suppress a no-rule torrent while it hasn't changed since we skipped it.
                    if (_skipped.TryGetValue(hash, out var skippedAt) && skippedAt == observed)
                    {
                        continue;
                    }

                    var hadProcessed = _processed!.TryGetValue(hash, out var processed);
                    var changed = !hadProcessed || processed != observed;

                    if (observed.IsDownloaded && changed)
                    {
                        changes.Add(hash);
                    }
                    else if (changed)
                    {
                        // Non-actionable change (e.g. a regression to a non-downloaded
                        // state): advance the baseline so a later completion is detected.
                        _processed![hash] = observed;
                        dirty.Add(observed.ToSnapshot(hash));
                    }
                }

                await _store.UpsertAsync(dirty, cancellationToken);

                if (removed.Count > 0)
                {
                    await _store.RemoveAsync(removed, cancellationToken);
                }

                return changes;
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task ConfirmProcessedAsync(IEnumerable<string> hashes, CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken);

            try
            {
                await EnsureLoadedAsync(cancellationToken);

                var dirty = new List<TorrentStateSnapshot>();

                foreach (var hash in hashes)
                {
                    // A confirmed torrent matched a rule, so it is no longer skipped.
                    _skipped.Remove(hash);

                    if (!_observed.TryGetValue(hash, out var observed))
                    {
                        continue;
                    }

                    if (!_processed!.TryGetValue(hash, out var processed) || processed != observed)
                    {
                        _processed[hash] = observed;
                        dirty.Add(observed.ToSnapshot(hash));
                    }
                }

                await _store.UpsertAsync(dirty, cancellationToken);
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task MarkSkippedAsync(IEnumerable<string> hashes, CancellationToken cancellationToken)
        {
            await _sync.WaitAsync(cancellationToken);

            try
            {
                await EnsureLoadedAsync(cancellationToken);

                foreach (var hash in hashes)
                {
                    if (_observed.TryGetValue(hash, out var observed))
                    {
                        _skipped[hash] = observed;
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
            await _sync.WaitAsync(cancellationToken);

            try
            {
                _skipped.Clear();
            }
            finally
            {
                _sync.Release();
            }
        }

        private void MergeObserved(MainData mainData)
        {
            if (mainData.Torrents is null)
            {
                return;
            }

            foreach (var (hash, partial) in mainData.Torrents)
            {
                // qBittorrent sends only changed fields on partial updates; a null field
                // means "unchanged", so fall back to the last observed value. Every known
                // torrent is present in _observed (seeded on startup, updated each sync).
                _observed.TryGetValue(hash, out var prev);

                var isDownloaded = partial.State?.IsDownloaded() ?? prev.IsDownloaded;
                var size = partial.Size ?? prev.Size;

                _observed[hash] = new Snapshot(isDownloaded, size);
            }
        }

        /// <summary>
        /// Determines which tracked torrents are gone: those qBittorrent explicitly
        /// reports as removed, plus — on a full sync, which is authoritative — any we
        /// track that are absent from it (e.g. removed while the app was down). This
        /// keeps the in-memory and persisted state from growing without bound.
        /// </summary>
        private List<string> CollectRemoved(MainData mainData)
        {
            var removed = new HashSet<string>(StringComparer.Ordinal);

            if (mainData.TorrentsRemoved is { } explicitlyRemoved)
            {
                foreach (var hash in explicitlyRemoved)
                {
                    removed.Add(hash);
                }
            }

            if (mainData.FullUpdateEnabled == true)
            {
                var present = mainData.Torrents is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(mainData.Torrents.Keys, StringComparer.Ordinal);

                foreach (var hash in _observed.Keys)
                {
                    if (!present.Contains(hash))
                    {
                        removed.Add(hash);
                    }
                }
            }

            return [.. removed];
        }

        private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (_processed is not null)
            {
                return;
            }

            var persisted = await _store.LoadAllAsync(cancellationToken);
            _processed = persisted.ToDictionary(
                kv => kv.Key,
                kv => new Snapshot(kv.Value.IsDownloaded, kv.Value.Size));

            // Seed observed with the persisted baseline so partial updates have a base to
            // merge onto and nothing is spuriously re-emitted before the first sync.
            foreach (var (hash, snapshot) in _processed)
            {
                _observed[hash] = snapshot;
            }
        }

        private readonly record struct Snapshot(bool IsDownloaded, long? Size)
        {
            public TorrentStateSnapshot ToSnapshot(string hash) => new(hash, IsDownloaded, Size);
        }
    }
}
