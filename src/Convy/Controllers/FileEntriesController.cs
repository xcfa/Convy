using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convy.Data.Context;
using Convy.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Convy.Controllers;

[ApiController]
[Route("api/v1/files")]
public class FileEntriesController : ControllerBase
{
    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;

    public FileEntriesController(IDbContextFactory<ConvyDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Returns linked file entries with optional filtering.
    /// All filter parameters are combined with AND.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<FileEntryDto>>> GetAll(
        [FromQuery] FileEntryFilter filter,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

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

        var results = await query
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
            .ToListAsync(cancellationToken);

        return results;
    }
}

public class FileEntryFilter
{
    /// <summary>Exact info hash.</summary>
    public string? InfoHash { get; set; }

    /// <summary>Substring match on torrent name.</summary>
    public string? TorrentName { get; set; }

    /// <summary>Substring match on file path or target path.</summary>
    public string? Path { get; set; }

    /// <summary>Exact date (entries linked on that day). Overrides From/To.</summary>
    public DateTimeOffset? Date { get; set; }

    /// <summary>Start of the date range (inclusive).</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>End of the date range (exclusive).</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>Maximum number of entries to return. Default 500.</summary>
    public int Limit { get; set; } = 500;
}

public class FileEntryDto
{
    public required string InfoHash { get; set; }
    public required string FilePath { get; set; }
    public required string TargetPath { get; set; }
    public string? TorrentName { get; set; }
    public DateTimeOffset LinkedDate { get; set; }
}
