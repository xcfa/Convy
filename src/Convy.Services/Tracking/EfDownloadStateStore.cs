using Convy.Data.Context;
using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Convy.Services.Tracking
{
    /// <summary>EF Core implementation of <see cref="IDownloadStateStore"/> over <see cref="ConvyDbContext"/>.</summary>
    public sealed class EfDownloadStateStore : IDownloadStateStore
    {
        private readonly IDbContextFactory<ConvyDbContext> _dbFactory;

        public EfDownloadStateStore(IDbContextFactory<ConvyDbContext> dbFactory) => _dbFactory = dbFactory;

        public async Task<IReadOnlyList<DownloadStateSnapshot>> LoadAllAsync(CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            return await db.DownloadStates
                .AsNoTracking()
                .Select(e => new DownloadStateSnapshot(e.Provider, e.ItemRef, e.IsDownloaded, e.Size))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task UpsertAsync(IReadOnlyCollection<DownloadStateSnapshot> snapshots, CancellationToken cancellationToken)
        {
            if (snapshots.Count == 0)
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            foreach (var group in snapshots.GroupBy(s => s.Provider))
            {
                var refs = group.Select(s => s.ItemRef).ToList();
                var existing = await db.DownloadStates
                    .Where(e => e.Provider == group.Key && refs.Contains(e.ItemRef))
                    .ToDictionaryAsync(e => e.ItemRef, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var snapshot in group)
                {
                    if (existing.TryGetValue(snapshot.ItemRef, out var entry))
                    {
                        entry.IsDownloaded = snapshot.IsDownloaded;
                        entry.Size = snapshot.Size;
                        entry.UpdatedDate = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        db.DownloadStates.Add(new DownloadStateEntry
                        {
                            Provider = snapshot.Provider,
                            ItemRef = snapshot.ItemRef,
                            IsDownloaded = snapshot.IsDownloaded,
                            Size = snapshot.Size,
                            UpdatedDate = DateTimeOffset.UtcNow,
                        });
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task RemoveAsync(string provider, IReadOnlyCollection<string> itemRefs, CancellationToken cancellationToken)
        {
            if (itemRefs.Count == 0)
                return;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            await db.DownloadStates
                .Where(e => e.Provider == provider && itemRefs.Contains(e.ItemRef))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
