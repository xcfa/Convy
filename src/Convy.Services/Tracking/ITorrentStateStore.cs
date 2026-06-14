namespace Convy.Services.Tracking
{
    /// <summary>The persisted last-known state of one torrent.</summary>
    public readonly record struct TorrentStateSnapshot(string Hash, bool IsDownloaded, long? Size);

    /// <summary>
    /// Persistence boundary for the state tracker. Abstracted so the tracker's
    /// merge/diff logic can be unit-tested without a database.
    /// </summary>
    public interface ITorrentStateStore
    {
        Task<IReadOnlyDictionary<string, TorrentStateSnapshot>> LoadAllAsync(CancellationToken cancellationToken);

        Task UpsertAsync(IReadOnlyCollection<TorrentStateSnapshot> snapshots, CancellationToken cancellationToken);

        Task RemoveAsync(IReadOnlyCollection<string> hashes, CancellationToken cancellationToken);
    }
}
