using System.Text.Json;
using Convy.Data.Context;
using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Convy.Services.Downloaders.Opds;

/// <summary>State of a book download.</summary>
public enum OpdsDownloadState
{
    Queued,
    Downloading,

    /// <summary>The last attempt failed; it is tried again at <see cref="OpdsDownload.NextAttemptAt"/>.</summary>
    Retrying,

    Completed,
    Failed,
    Cancelled,
}

/// <summary>One file of a book download.</summary>
/// <param name="Name">File name inside the book's folder.</param>
/// <param name="Url">Download address.</param>
/// <param name="Size">Size the catalog announced, when known.</param>
/// <param name="Unzip">Store the single file inside the downloaded zip.</param>
/// <param name="Preference">Rank among the preferred formats, or <c>null</c>.</param>
/// <param name="Wanted">The file is to be downloaded.</param>
/// <param name="Done">The file is complete on disk.</param>
/// <param name="Bytes">Size on disk once done.</param>
/// <param name="Failed">The file failed for good (a fallback took its place).</param>
public sealed record OpdsDownloadFile(
    string Name, string Url, long? Size, bool Unzip, int? Preference, bool Wanted, bool Done = false, long Bytes = 0, bool Failed = false);

/// <summary>A book Convy downloads from an OPDS catalog.</summary>
public sealed record OpdsDownload
{
    public required string ItemRef { get; init; }
    public required string CatalogId { get; init; }
    public required string Folder { get; init; }
    public required string SavePath { get; init; }
    public string? Category { get; init; }
    public required IReadOnlyList<OpdsDownloadFile> Files { get; init; }
    public bool Fallback { get; init; }
    public required OpdsDownloadState State { get; init; }
    public string? Error { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public IEnumerable<OpdsDownloadFile> WantedFiles => Files.Where(f => f.Wanted);

    /// <summary>Whether the download is still to be worked on.</summary>
    public bool IsActive => State is OpdsDownloadState.Queued or OpdsDownloadState.Downloading or OpdsDownloadState.Retrying;
}

/// <summary>Persistence of book downloads.</summary>
public interface IOpdsDownloadStore
{
    Task<OpdsDownload?> GetAsync(string itemRef, CancellationToken cancellationToken);

    Task<IReadOnlyList<OpdsDownload>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates the download or replaces the stored one, whatever its state.</summary>
    Task SaveAsync(OpdsDownload download, CancellationToken cancellationToken);

    /// <summary>
    /// Saves <paramref name="download"/> only if the stored state is still <paramref name="expected"/>;
    /// <c>false</c> when another writer (a cancellation) got there first.
    /// </summary>
    Task<bool> TryUpdateAsync(OpdsDownload download, OpdsDownloadState expected, CancellationToken cancellationToken);
}

/// <summary>EF Core implementation of <see cref="IOpdsDownloadStore"/>.</summary>
public sealed class EfOpdsDownloadStore : IOpdsDownloadStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;

    public EfOpdsDownloadStore(IDbContextFactory<ConvyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<OpdsDownload?> GetAsync(string itemRef, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.OpdsDownloads.AsNoTracking()
            .FirstOrDefaultAsync(d => d.ItemRef == itemRef, cancellationToken)
            .ConfigureAwait(false);
        return entry is null ? null : ToDownload(entry);
    }

    public async Task<IReadOnlyList<OpdsDownload>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entries = await db.OpdsDownloads.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return entries.Select(ToDownload).ToList();
    }

    public async Task SaveAsync(OpdsDownload download, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.OpdsDownloads
            .FirstOrDefaultAsync(d => d.ItemRef == download.ItemRef, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            entry = new OpdsDownloadEntry
            {
                ItemRef = download.ItemRef,
                CatalogId = download.CatalogId,
                Folder = download.Folder,
                SavePath = download.SavePath,
                FilesJson = "[]",
                State = "queued",
            };
            db.OpdsDownloads.Add(entry);
        }

        Apply(download, entry);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryUpdateAsync(OpdsDownload download, OpdsDownloadState expected, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.OpdsDownloads
            .FirstOrDefaultAsync(d => d.ItemRef == download.ItemRef, cancellationToken)
            .ConfigureAwait(false);

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

    private static string Name(OpdsDownloadState state) => state.ToString().ToLowerInvariant();

    private static void Apply(OpdsDownload download, OpdsDownloadEntry entry)
    {
        entry.CatalogId = download.CatalogId;
        entry.Folder = download.Folder;
        entry.SavePath = download.SavePath;
        entry.Category = download.Category;
        entry.FilesJson = JsonSerializer.Serialize(download.Files, Json);
        entry.Fallback = download.Fallback;
        entry.State = Name(download.State);
        entry.Error = download.Error;
        entry.Attempts = download.Attempts;
        entry.NextAttemptAt = download.NextAttemptAt?.ToUnixTimeSeconds();
        entry.CreatedAt = download.CreatedAt;
        entry.UpdatedAt = download.UpdatedAt;
    }

    private static OpdsDownload ToDownload(OpdsDownloadEntry entry) => new()
    {
        ItemRef = entry.ItemRef,
        CatalogId = entry.CatalogId,
        Folder = entry.Folder,
        SavePath = entry.SavePath,
        Category = entry.Category,
        Files = JsonSerializer.Deserialize<List<OpdsDownloadFile>>(entry.FilesJson, Json) ?? [],
        Fallback = entry.Fallback,
        State = Enum.TryParse<OpdsDownloadState>(entry.State, ignoreCase: true, out var state) ? state : OpdsDownloadState.Failed,
        Error = entry.Error,
        Attempts = entry.Attempts,
        NextAttemptAt = entry.NextAttemptAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
    };
}
