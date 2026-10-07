using Convy.Services.Downloads;

namespace Convy.Services.Tracking
{
    /// <summary>
    /// Remembers each downloader's items across cycles (and across restarts, via
    /// <see cref="IDownloadStateStore"/>) and reports only the items that changed in a way
    /// we care about. Items are tracked per provider; providers never affect each other.
    /// </summary>
    public interface IDownloadStateTracker
    {
        /// <summary>
        /// Applies the complete current item list of <paramref name="provider"/> and returns the
        /// references of items that need (re)processing. Items absent from the list are
        /// forgotten. The persisted "processed" baseline is NOT advanced for the returned
        /// items — call <see cref="ConfirmProcessedAsync"/> once an item has been fully
        /// handled, otherwise it is re-emitted on the next cycle (so a transient failure,
        /// e.g. an unmounted directory, is retried).
        /// </summary>
        Task<IReadOnlyList<string>> ApplyAsync(string provider, IReadOnlyCollection<DownloadItem> items, CancellationToken cancellationToken);

        /// <summary>
        /// Marks the given items as successfully processed, advancing their persisted
        /// baseline to the last observed state so they are not re-emitted.
        /// </summary>
        Task ConfirmProcessedAsync(string provider, IEnumerable<string> itemRefs, CancellationToken cancellationToken);

        /// <summary>
        /// Marks items that matched no rule as skipped (in-memory only). They are not
        /// re-emitted until their tracked state changes, the rules change
        /// (<see cref="ClearSkippedAsync"/>), or the process restarts — so a no-rule item is
        /// never persisted as "done" and is re-evaluated cheaply against the latest rules.
        /// </summary>
        Task MarkSkippedAsync(string provider, IEnumerable<string> itemRefs, CancellationToken cancellationToken);

        /// <summary>
        /// Forgets all skipped items so they are re-emitted and re-evaluated. Called
        /// when the routing rules change.
        /// </summary>
        Task ClearSkippedAsync(CancellationToken cancellationToken);
    }
}
