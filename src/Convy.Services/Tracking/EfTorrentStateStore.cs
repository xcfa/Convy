using Convy.Data.Context;
using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Convy.Services.Tracking
{
    /// <summary>EF Core implementation of <see cref="ITorrentStateStore"/> over <see cref="ConvyDbContext"/>.</summary>
    public sealed class EfTorrentStateStore : ITorrentStateStore
    {
        private readonly IDbContextFactory<ConvyDbContext> _dbFactory;

        public EfTorrentStateStore(IDbContextFactory<ConvyDbContext> dbFactory) => _dbFactory = dbFactory;

        public async Task<IReadOnlyDictionary<string, TorrentStateSnapshot>> LoadAllAsync(CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            return await db.TorrentStates
                .AsNoTracking()
                .ToDictionaryAsync(
                    e => e.InfoHash,
                    e => new TorrentStateSnapshot(e.InfoHash, e.IsDownloaded, e.Size),
                    cancellationToken);
        }

        public async Task UpsertAsync(IReadOnlyCollection<TorrentStateSnapshot> snapshots, CancellationToken cancellationToken)
        {
            if (snapshots.Count == 0)
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var hashes = snapshots.Select(s => s.Hash).ToList();
            var existing = await db.TorrentStates
                .Where(e => hashes.Contains(e.InfoHash))
                .ToDictionaryAsync(e => e.InfoHash, cancellationToken);

            foreach (var snapshot in snapshots)
            {
                if (existing.TryGetValue(snapshot.Hash, out var entry))
                {
                    entry.IsDownloaded = snapshot.IsDownloaded;
                    entry.Size = snapshot.Size;
                    entry.UpdatedDate = DateTimeOffset.UtcNow;
                }
                else
                {
                    db.TorrentStates.Add(new TorrentStateEntry
                    {
                        InfoHash = snapshot.Hash,
                        IsDownloaded = snapshot.IsDownloaded,
                        Size = snapshot.Size,
                        UpdatedDate = DateTimeOffset.UtcNow,
                    });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task RemoveAsync(IReadOnlyCollection<string> hashes, CancellationToken cancellationToken)
        {
            if (hashes.Count == 0)
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            await db.TorrentStates
                .Where(e => hashes.Contains(e.InfoHash))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }
}
