using System.Security.Cryptography;
using System.Text.Json;
using Convy.Data.Context;
using Convy.Data.Entities;
using Convy.Sources;
using Microsoft.EntityFrameworkCore;

namespace Convy.Services.Media;

/// <summary>A search in progress: what is searched and which sources are left.</summary>
public sealed record SearchSession(
    string Id,
    string Category,
    IReadOnlyList<string> Queries,
    IReadOnlyList<string> Sources,
    int NextSourceIndex,
    DateTimeOffset ExpiresAt);

/// <summary>A cached search result.</summary>
public sealed record CachedResult
{
    public required string Id { get; init; }
    public required string SearchId { get; init; }
    public required Protocol Protocol { get; init; }
    public required string Title { get; init; }
    public long? SizeBytes { get; init; }
    public int? FileCount { get; init; }
    public required Availability Availability { get; init; }

    /// <summary>Every source that returned the result, in priority order.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    public required IReadOnlyList<string> MatchedQueries { get; init; }

    /// <summary>Identity used to merge results across sources, variants and steps.</summary>
    public required string DedupKey { get; init; }

    /// <summary>The source the result is listed and resolved through.</summary>
    public required string SourceId { get; init; }

    public required string ContentId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Storage of search sessions, results and file lists, with expiry.</summary>
public interface ISearchCache
{
    Task CreateSessionAsync(SearchSession session, CancellationToken cancellationToken);

    /// <summary>The session, or <c>null</c> when unknown or expired.</summary>
    Task<SearchSession?> GetSessionAsync(string id, CancellationToken cancellationToken);

    Task UpdateSessionAsync(string id, int nextSourceIndex, CancellationToken cancellationToken);

    Task<IReadOnlyList<CachedResult>> GetSessionResultsAsync(string searchId, CancellationToken cancellationToken);

    /// <summary>Inserts new results and updates existing ones (merged sources/queries).</summary>
    Task SaveResultsAsync(IReadOnlyCollection<CachedResult> results, CancellationToken cancellationToken);

    /// <summary>The result, or <c>null</c> when unknown or expired.</summary>
    Task<CachedResult?> GetResultAsync(string id, CancellationToken cancellationToken);

    /// <summary>The cached file list of a result, or <c>null</c>.</summary>
    Task<FileListing?> GetListingAsync(string resultId, CancellationToken cancellationToken);

    Task SaveListingAsync(string resultId, FileListing listing, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Deletes everything that has expired.</summary>
    Task PurgeExpiredAsync(CancellationToken cancellationToken);
}

/// <summary>Opaque ids for searches (<c>s_…</c>) and results (<c>r_…</c>).</summary>
public static class CacheIds
{
    public static string NewSearchId() => "s_" + RandomHex();

    public static string NewResultId() => "r_" + RandomHex();

    private static string RandomHex() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
}

/// <summary>EF Core implementation of <see cref="ISearchCache"/> (SQLite).</summary>
public sealed class EfSearchCache : ISearchCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public EfSearchCache(IDbContextFactory<ConvyDbContext> dbFactory, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task CreateSessionAsync(SearchSession session, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.SearchSessions.Add(new SearchSessionEntry
        {
            Id = session.Id,
            Category = session.Category,
            QueriesJson = JsonSerializer.Serialize(session.Queries, Json),
            SourcesJson = JsonSerializer.Serialize(session.Sources, Json),
            NextSourceIndex = session.NextSourceIndex,
            CreatedAt = Now(),
            ExpiresAt = session.ExpiresAt.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchSession?> GetSessionAsync(string id, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = Now();
        var entry = await db.SearchSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.ExpiresAt > now, cancellationToken)
            .ConfigureAwait(false);

        return entry is null
            ? null
            : new SearchSession(
                entry.Id,
                entry.Category,
                JsonSerializer.Deserialize<List<string>>(entry.QueriesJson, Json) ?? [],
                JsonSerializer.Deserialize<List<string>>(entry.SourcesJson, Json) ?? [],
                entry.NextSourceIndex,
                DateTimeOffset.FromUnixTimeSeconds(entry.ExpiresAt));
    }

    public async Task UpdateSessionAsync(string id, int nextSourceIndex, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.SearchSessions
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextSourceIndex, nextSourceIndex), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CachedResult>> GetSessionResultsAsync(string searchId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entries = await db.SearchResults.AsNoTracking()
            .Where(r => r.SearchId == searchId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entries.Select(ToResult).ToList();
    }

    public async Task SaveResultsAsync(IReadOnlyCollection<CachedResult> results, CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = results.Select(r => r.Id).ToList();
        var existing = await db.SearchResults
            .Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken)
            .ConfigureAwait(false);

        foreach (var result in results)
        {
            if (existing.TryGetValue(result.Id, out var entry))
            {
                Apply(result, entry);
            }
            else
            {
                entry = new SearchResultEntry
                {
                    Id = result.Id,
                    SearchId = result.SearchId,
                    Protocol = ProtocolNames.ToName(result.Protocol),
                    Title = result.Title,
                    AvailabilityJson = "{}",
                    SourcesJson = "[]",
                    MatchedQueriesJson = "[]",
                    SourceId = result.SourceId,
                    ContentId = result.ContentId,
                    CreatedAt = Now(),
                };
                Apply(result, entry);
                db.SearchResults.Add(entry);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CachedResult?> GetResultAsync(string id, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = Now();
        var entry = await db.SearchResults.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.ExpiresAt > now, cancellationToken)
            .ConfigureAwait(false);

        return entry is null ? null : ToResult(entry);
    }

    public async Task<FileListing?> GetListingAsync(string resultId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = Now();
        var entry = await db.FileListings.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ResultId == resultId && l.ExpiresAt > now, cancellationToken)
            .ConfigureAwait(false);

        return entry is null
            ? null
            : new FileListing(JsonSerializer.Deserialize<List<ListedFile>>(entry.FilesJson, Json) ?? [], entry.TorrentFile);
    }

    public async Task SaveListingAsync(string resultId, FileListing listing, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await db.FileListings
            .FirstOrDefaultAsync(l => l.ResultId == resultId, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            entry = new FileListingEntry { ResultId = resultId, FilesJson = "[]" };
            db.FileListings.Add(entry);
        }

        entry.FilesJson = JsonSerializer.Serialize(listing.Files, Json);
        entry.TorrentFile = listing.TorrentFile;
        entry.CreatedAt = Now();
        entry.ExpiresAt = expiresAt.ToUnixTimeSeconds();

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = Now();

        await db.FileListings.Where(l => l.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.SearchResults.Where(r => r.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.SearchSessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private long Now() => _timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private static void Apply(CachedResult result, SearchResultEntry entry)
    {
        entry.Title = result.Title;
        entry.SizeBytes = result.SizeBytes;
        entry.FileCount = result.FileCount;
        entry.AvailabilityJson = JsonSerializer.Serialize(result.Availability, Json);
        entry.SourcesJson = JsonSerializer.Serialize(result.Sources, Json);
        entry.MatchedQueriesJson = JsonSerializer.Serialize(result.MatchedQueries, Json);
        entry.DedupKey = result.DedupKey;
        entry.ExpiresAt = result.ExpiresAt.ToUnixTimeSeconds();
    }

    private static CachedResult ToResult(SearchResultEntry entry) => new()
    {
        Id = entry.Id,
        SearchId = entry.SearchId,
        Protocol = ProtocolNames.Parse(entry.Protocol),
        Title = entry.Title,
        SizeBytes = entry.SizeBytes,
        FileCount = entry.FileCount,
        Availability = JsonSerializer.Deserialize<Availability>(entry.AvailabilityJson, Json) ?? new Availability(),
        Sources = JsonSerializer.Deserialize<List<string>>(entry.SourcesJson, Json) ?? [],
        MatchedQueries = JsonSerializer.Deserialize<List<string>>(entry.MatchedQueriesJson, Json) ?? [],
        DedupKey = entry.DedupKey ?? entry.Id,
        SourceId = entry.SourceId,
        ContentId = entry.ContentId,
        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(entry.ExpiresAt),
    };
}

/// <summary>Wire names of <see cref="Protocol"/> (<c>torrent</c>, <c>soulseek</c>).</summary>
public static class ProtocolNames
{
    public static string ToName(Protocol protocol) => protocol.ToString().ToLowerInvariant();

    public static Protocol Parse(string name) => Enum.Parse<Protocol>(name, ignoreCase: true);
}
