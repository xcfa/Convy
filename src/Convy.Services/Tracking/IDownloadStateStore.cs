namespace Convy.Services.Tracking
{
    /// <summary>The persisted last-known state of one download item.</summary>
    public readonly record struct DownloadStateSnapshot(string Provider, string ItemRef, bool IsDownloaded, long? Size);

    /// <summary>
    /// Persistence boundary for the state tracker. Abstracted so the tracker's
    /// diff logic can be unit-tested without a database.
    /// </summary>
    public interface IDownloadStateStore
    {
        Task<IReadOnlyList<DownloadStateSnapshot>> LoadAllAsync(CancellationToken cancellationToken);

        Task UpsertAsync(IReadOnlyCollection<DownloadStateSnapshot> snapshots, CancellationToken cancellationToken);

        Task RemoveAsync(string provider, IReadOnlyCollection<string> itemRefs, CancellationToken cancellationToken);
    }
}
