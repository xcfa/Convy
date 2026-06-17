using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Banned.Qbittorrent.Models.Sync;

namespace Convy.Services.Tracking
{
    /// <summary>
    /// Accumulates qBittorrent sync data across cycles (and across restarts, via
    /// <see cref="ITorrentStateStore"/>) and reports only the torrents that changed
    /// in a way we care about.
    /// </summary>
    public interface ITorrentStateTracker
    {
        /// <summary>
        /// Merges one <see cref="MainData"/> response into the observed state and
        /// returns the hashes of torrents that need (re)processing. The persisted
        /// "processed" baseline is NOT advanced for the returned hashes — call
        /// <see cref="ConfirmProcessedAsync"/> once a torrent has been fully handled,
        /// otherwise it is re-emitted on the next cycle (so a transient failure, e.g.
        /// an unmounted directory, is retried).
        /// </summary>
        Task<IReadOnlyList<string>> ApplyAsync(MainData mainData, CancellationToken cancellationToken);

        /// <summary>
        /// Marks the given torrents as successfully processed, advancing their
        /// persisted baseline to the last observed state so they are not re-emitted.
        /// </summary>
        Task ConfirmProcessedAsync(IEnumerable<string> hashes, CancellationToken cancellationToken);

        /// <summary>
        /// Marks torrents that matched no rule as skipped (in-memory only). They are not
        /// re-emitted until their tracked state changes, the rules change
        /// (<see cref="ClearSkipped"/>), or the process restarts — so a no-rule torrent is
        /// never persisted as "done" and is re-evaluated cheaply against the latest rules.
        /// </summary>
        Task MarkSkippedAsync(IEnumerable<string> hashes, CancellationToken cancellationToken);

        /// <summary>
        /// Forgets all skipped torrents so they are re-emitted and re-evaluated. Called
        /// when the routing rules change.
        /// </summary>
        Task ClearSkippedAsync(CancellationToken cancellationToken);
    }
}
