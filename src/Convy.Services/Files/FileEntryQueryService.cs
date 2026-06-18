using Convy.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Convy.Services.Files;

/// <inheritdoc />
public sealed class FileEntryQueryService : IFileEntryQueryService
{
    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;

    public FileEntryQueryService(IDbContextFactory<ConvyDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public async Task<List<FileEntryDto>> QueryAsync(
        FileEntryFilter filter, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var query = db.FileEntries.AsQueryable();

        if (!string.IsNullOrEmpty(filter.InfoHash))
        {
            query = query.Where(e => e.InfoHash == filter.InfoHash);
        }

        if (!string.IsNullOrEmpty(filter.TorrentName))
        {
            query = query.Where(e => e.TorrentName != null && e.TorrentName.Contains(filter.TorrentName));
        }

        if (!string.IsNullOrEmpty(filter.Path))
        {
            query = query.Where(e => e.FilePath.Contains(filter.Path) || e.TargetPath.Contains(filter.Path));
        }

        if (filter.Date.HasValue)
        {
            var day = filter.Date.Value.Date;
            var nextDay = day.AddDays(1);
            query = query.Where(e => e.LinkedDate >= day && e.LinkedDate < nextDay);
        }
        else
        {
            if (filter.From.HasValue)
            {
                query = query.Where(e => e.LinkedDate >= filter.From.Value);
            }

            if (filter.To.HasValue)
            {
                query = query.Where(e => e.LinkedDate < filter.To.Value);
            }
        }

        return await query
            .OrderByDescending(e => e.LinkedDate)
            .Take(filter.Limit)
            .Select(e => new FileEntryDto
            {
                InfoHash = e.InfoHash,
                FilePath = e.FilePath,
                TargetPath = e.TargetPath,
                TorrentName = e.TorrentName,
                LinkedDate = e.LinkedDate,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
