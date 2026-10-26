using Convy.Data.Context;
using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Convy.Services.Downloaders.Gallery;

/// <summary>State of a gallery download.</summary>
public enum GalleryDownloadState
{
    Queued,
    Downloading,

    /// <summary>The last attempt failed; it is tried again at <see cref="GalleryDownload.NextAttemptAt"/>.</summary>
    Retrying,

    Completed,
    Failed,
    Cancelled,
}

/// <summary>A gallery Convy downloads from a site source into a CBZ.</summary>
public sealed record GalleryDownload
{
    public required string ItemRef { get; init; }
    public required string SourceId { get; init; }
    public required string ContentId { get; init; }
    public required string Title { get; init; }
    public required string Folder { get; init; }
    public required string FileName { get; init; }
    public required string SavePath { get; init; }
    public string? Category { get; init; }
    public int? PagesTotal { get; init; }
    public int PagesDone { get; init; }
    public long Bytes { get; init; }
    public required GalleryDownloadState State { get; init; }
    public string? Error { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The archive's path once <see cref="State"/> is <see cref="GalleryDownloadState.Completed"/>.</summary>
    public string ArchivePath => Path.Combine(SavePath, Folder, FileName);

    /// <summary>Where the pages are kept while downloading.</summary>
    public string WorkPath => Path.Combine(SavePath, ".pages");

    /// <summary>Whether the download is still to be worked on.</summary>
    public bool IsActive => State is GalleryDownloadState.Queued or GalleryDownloadState.Downloading or GalleryDownloadState.Retrying;
}

/// <summary>Persistence of gallery downloads.</summary>
public interface IGalleryDownloadStore
{
    Task<GalleryDownload?> GetAsync(string itemRef, CancellationToken cancellationToken);

    Task<IReadOnlyList<GalleryDownload>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates the download or replaces the stored one, whatever its state.</summary>
    Task SaveAsync(GalleryDownload download, CancellationToken cancellationToken);

    /// <summary>
    /// Saves <paramref name="download"/> only if the stored state is still <paramref name="expected"/>;
    /// <c>false</c> when another writer (a cancellation) got there first.
    /// </summary>
    Task<bool> TryUpdateAsync(GalleryDownload download, GalleryDownloadState expected, CancellationToken cancellationToken);
}

/// <summary>EF Core implementation of <see cref="IGalleryDownloadStore"/>.</summary>
public sealed class EfGalleryDownloadStore : IGalleryDownloadStore
{
    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;

    public EfGalleryDownloadStore(IDbContextFactory<ConvyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<GalleryDownload?> GetAsync(string itemRef, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.GalleryDownloads.AsNoTracking().FirstOrDefaultAsync(d => d.ItemRef == itemRef, cancellationToken).ConfigureAwait(false);
        return entry is null ? null : ToDownload(entry);
    }

    public async Task<IReadOnlyList<GalleryDownload>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entries = await db.GalleryDownloads.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return entries.Select(ToDownload).ToList();
    }

    public async Task SaveAsync(GalleryDownload download, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.GalleryDownloads.FirstOrDefaultAsync(d => d.ItemRef == download.ItemRef, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            entry = new GalleryDownloadEntry
            {
                ItemRef = download.ItemRef,
                SourceId = download.SourceId,
                ContentId = download.ContentId,
                Title = download.Title,
                Folder = download.Folder,
                FileName = download.FileName,
                SavePath = download.SavePath,
                State = "queued",
            };
            db.GalleryDownloads.Add(entry);
        }

        Apply(download, entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryUpdateAsync(GalleryDownload download, GalleryDownloadState expected, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.GalleryDownloads.FirstOrDefaultAsync(d => d.ItemRef == download.ItemRef, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.State != Name(expected))
        {
            return false;
        }

        Apply(download, entry);
        try
        {
            // State is a concurrency token: the UPDATE only applies if nobody changed it since the read.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static string Name(GalleryDownloadState state) => state.ToString().ToLowerInvariant();

    private static void Apply(GalleryDownload download, GalleryDownloadEntry entry)
    {
        entry.SourceId = download.SourceId;
        entry.ContentId = download.ContentId;
        entry.Title = download.Title;
        entry.Folder = download.Folder;
        entry.FileName = download.FileName;
        entry.SavePath = download.SavePath;
        entry.Category = download.Category;
        entry.PagesTotal = download.PagesTotal;
        entry.PagesDone = download.PagesDone;
        entry.Bytes = download.Bytes;
        entry.State = Name(download.State);
        entry.Error = download.Error;
        entry.Attempts = download.Attempts;
        entry.NextAttemptAt = download.NextAttemptAt?.ToUnixTimeSeconds();
        entry.CreatedAt = download.CreatedAt;
        entry.UpdatedAt = download.UpdatedAt;
    }

    private static GalleryDownload ToDownload(GalleryDownloadEntry entry) => new()
    {
        ItemRef = entry.ItemRef,
        SourceId = entry.SourceId,
        ContentId = entry.ContentId,
        Title = entry.Title,
        Folder = entry.Folder,
        FileName = entry.FileName,
        SavePath = entry.SavePath,
        Category = entry.Category,
        PagesTotal = entry.PagesTotal,
        PagesDone = entry.PagesDone,
        Bytes = entry.Bytes,
        State = Enum.TryParse<GalleryDownloadState>(entry.State, ignoreCase: true, out var state) ? state : GalleryDownloadState.Failed,
        Error = entry.Error,
        Attempts = entry.Attempts,
        NextAttemptAt = entry.NextAttemptAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
    };
}
