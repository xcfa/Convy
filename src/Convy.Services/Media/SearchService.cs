using Convy.Services.Webhooks;
using Convy.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Media;

/// <summary>Outcome of one source in a search step, as reported to the agent.</summary>
public enum SourceSearchStatus
{
    Ok,
    Empty,
    Timeout,
    AuthFailed,
    Error,
}

/// <summary>
/// Searches the sources of a category in batches. Each step searches the next
/// <see cref="SearchOptions.BatchSize"/> sources with every query variant in parallel,
/// merges and ranks what they found and caches it; whether to go on with
/// <c>search_next</c> is the agent's decision.
/// </summary>
public sealed class SearchService
{
    private readonly CategoryCatalog _catalog;
    private readonly ISourceRegistry _registry;
    private readonly ISearchCache _cache;
    private readonly ISourceHealth _health;
    private readonly IOptionsMonitor<SearchOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        CategoryCatalog catalog,
        ISourceRegistry registry,
        ISearchCache cache,
        ISourceHealth health,
        IOptionsMonitor<SearchOptions> options,
        TimeProvider timeProvider,
        ILogger<SearchService> logger)
    {
        _catalog = catalog;
        _registry = registry;
        _cache = cache;
        _health = health;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Starts a search and runs its first batch.</summary>
    /// <param name="categoryId">Category id.</param>
    /// <param name="queries">Variants of the title (spelling, dashes, original title).</param>
    /// <param name="sourceIds">Sources to search instead of the category's list.</param>
    /// <param name="cancellationToken">Cancellation of the request.</param>
    public async Task<SearchResponse> StartAsync(
        string categoryId,
        IReadOnlyList<string>? queries,
        IReadOnlyList<string>? sourceIds,
        CancellationToken cancellationToken)
    {
        var category = _catalog.Get(categoryId);
        var options = _options.CurrentValue;
        var variants = NormalizeQueries(queries, options.MaxQueryVariants);

        var all = await _registry.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        var (selected, rejected) = SelectSources(category, sourceIds, all);
        if (selected.Count == 0 && rejected.Count == 0)
        {
            throw new ConvyRequestException($"No source is available for category '{category.Id}'.");
        }

        await _cache.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);

        var session = new SearchSession(
            CacheIds.NewSearchId(),
            category.Id,
            variants,
            selected.Select(s => s.Id).ToList(),
            NextSourceIndex: 0,
            _timeProvider.GetUtcNow() + TimeSpan.FromHours(options.CacheTtlHours));
        await _cache.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);

        var response = await RunBatchAsync(session, category, cancellationToken).ConfigureAwait(false);

        // Sources the agent asked for that do not exist (or are disabled) are reported, not hidden.
        var unknown = rejected.Select(id => new SourceSearchStatusDto(id, "error", 0, "Unknown or disabled source."));
        return response with { Sources = response.Sources.Concat(unknown).ToList() };
    }

    /// <summary>Runs the next batch of sources of a search.</summary>
    public async Task<SearchResponse> NextAsync(string searchId, CancellationToken cancellationToken)
    {
        var session = await _cache.GetSessionAsync(searchId, cancellationToken).ConfigureAwait(false)
                      ?? throw new ConvyRequestException($"Search '{searchId}' is unknown or expired; start a new search.");

        if (session.NextSourceIndex >= session.Sources.Count)
        {
            return new SearchResponse(session.Id, [], [], 0, 0, HasMore: false, "Every source of this search has been searched.");
        }

        var category = _catalog.Get(session.Category);
        return await RunBatchAsync(session, category, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SearchResponse> RunBatchAsync(SearchSession session, Category category, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var batchSize = Math.Max(1, options.BatchSize);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.SourceTimeoutSeconds));

        var batch = session.Sources.Skip(session.NextSourceIndex).Take(batchSize).ToList();
        var nextIndex = session.NextSourceIndex + batch.Count;

        var available = await _registry.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        var searches = new List<Task<QueryOutcome>>();
        var statuses = new List<SourceSearchStatusDto>();

        foreach (var sourceId in batch)
        {
            var source = available.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                statuses.Add(new SourceSearchStatusDto(sourceId, "error", 0, "The source is no longer available."));
                continue;
            }

            for (var q = 0; q < session.Queries.Count; q++)
            {
                searches.Add(SearchOneAsync(source, new SearchRequest(session.Queries[q], category.SearchSettings), q, timeout, cancellationToken));
            }
        }

        var outcomes = await Task.WhenAll(searches).ConfigureAwait(false);

        foreach (var group in outcomes.GroupBy(o => o.Source.Id))
        {
            var status = Summarize(group.Key, group.ToList());
            _health.Report(status.Id, status.Status, status.Message);
            statuses.Add(status);
        }

        // Keep the batch order in the status list.
        statuses = statuses.OrderBy(s => batch.FindIndex(id => string.Equals(id, s.Id, StringComparison.OrdinalIgnoreCase))).ToList();

        var (shown, total, updated) = await MergeAsync(session, outcomes, cancellationToken).ConfigureAwait(false);
        await _cache.SaveResultsAsync(shown.Concat(updated).ToList(), cancellationToken).ConfigureAwait(false);
        await _cache.UpdateSessionAsync(session.Id, nextIndex, cancellationToken).ConfigureAwait(false);

        var note = shown.Count < total
            ? $"Showing {shown.Count} of {total} results; the rest were dropped. Narrow the queries to see others."
            : null;

        return new SearchResponse(
            session.Id,
            shown.Select(ToDto).ToList(),
            statuses,
            shown.Count,
            total,
            HasMore: nextIndex < session.Sources.Count,
            note);
    }

    /// <summary>
    /// Merges the batch's findings: one result per identity (info hash, user + folder),
    /// carrying every source and query variant that found it. Results already shown by an
    /// earlier step of the same search are not shown again; they only gain the new sources.
    /// </summary>
    private async Task<(List<CachedResult> Shown, int Total, List<CachedResult> Updated)> MergeAsync(
        SearchSession session, IReadOnlyList<QueryOutcome> outcomes, CancellationToken cancellationToken)
    {
        // Concurrent steps of one search may have stored the same identity twice; keep one.
        var previous = (await _cache.GetSessionResultsAsync(session.Id, cancellationToken).ConfigureAwait(false))
            .GroupBy(r => r.DedupKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var updated = new Dictionary<string, CachedResult>(StringComparer.Ordinal);
        var merged = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        var priority = session.Sources
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index, StringComparer.OrdinalIgnoreCase);

        var findings = outcomes
            .SelectMany(o => o.Results.Select(content => (o.Source, o.QueryIndex, Content: content)))
            .OrderBy(f => priority.GetValueOrDefault(f.Source.Id, int.MaxValue))
            .ThenBy(f => f.QueryIndex);

        foreach (var (source, queryIndex, content) in findings)
        {
            var key = content.DedupKey ?? $"{source.Id}\n{content.ContentId}";
            var query = session.Queries[queryIndex];

            if (previous.TryGetValue(key, out var earlier))
            {
                var current = updated.GetValueOrDefault(key) ?? earlier;
                updated[key] = current with
                {
                    Sources = AddDistinct(current.Sources, source.Id),
                    MatchedQueries = AddDistinct(current.MatchedQueries, query),
                };
                continue;
            }

            if (merged.TryGetValue(key, out var candidate))
            {
                candidate.Add(source.Id, query, content.Availability);
            }
            else
            {
                merged[key] = new Candidate(key, source, content, query, priority.GetValueOrDefault(source.Id, int.MaxValue));
            }
        }

        var options = _options.CurrentValue;
        var expiresAt = session.ExpiresAt;
        var ranked = merged.Values
            .OrderBy(c => c.Priority)
            .ThenByDescending(c => Rank(c.Availability))
            .ToList();

        var shown = ranked
            .Take(Math.Max(1, options.MaxResults))
            .Select(c => c.ToResult(session.Id, expiresAt))
            .ToList();

        return (shown, ranked.Count, updated.Values.ToList());
    }

    private async Task<QueryOutcome> SearchOneAsync(
        IContentSource source, SearchRequest request, int queryIndex, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var results = new List<ContentInfo>();
        try
        {
            await foreach (var content in source.SearchAsync(request, deadline.Token).WithCancellation(deadline.Token).ConfigureAwait(false))
            {
                results.Add(content);
            }

            return new QueryOutcome(source, queryIndex, results, SourceSearchStatus.Ok, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QueryOutcome(source, queryIndex, results, SourceSearchStatus.Timeout, $"No answer within {timeout.TotalSeconds:0} s.");
        }
        catch (SourceException ex)
        {
            var status = ex.Kind == SourceErrorKind.AuthFailed ? SourceSearchStatus.AuthFailed : SourceSearchStatus.Error;
            _logger.LogWarning("Source {Source} failed for '{Query}': {Message}", source.Id, request.Query, ex.Message);
            return new QueryOutcome(source, queryIndex, results, status, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Source {Source} failed for '{Query}'.", source.Id, request.Query);
            return new QueryOutcome(source, queryIndex, results, SourceSearchStatus.Error, ex.Message);
        }
    }

    /// <summary>
    /// One status per source: <c>ok</c> when any variant found something; otherwise the most
    /// telling failure (auth, error, timeout); <c>empty</c> only when every variant answered
    /// without results.
    /// </summary>
    private static SourceSearchStatusDto Summarize(string sourceId, IReadOnlyList<QueryOutcome> outcomes)
    {
        // Distinct contents: the same release found by several variants counts once.
        var found = outcomes
            .SelectMany(o => o.Results)
            .Select(c => c.DedupKey ?? c.ContentId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (found > 0)
        {
            return new SourceSearchStatusDto(sourceId, "ok", found, null);
        }

        foreach (var status in new[] { SourceSearchStatus.AuthFailed, SourceSearchStatus.Error, SourceSearchStatus.Timeout })
        {
            if (outcomes.FirstOrDefault(o => o.Status == status) is { } failed)
            {
                return new SourceSearchStatusDto(sourceId, ToName(status), 0, failed.Message);
            }
        }

        return new SourceSearchStatusDto(sourceId, "empty", 0, null);
    }

    public static string ToName(SourceSearchStatus status) => status switch
    {
        SourceSearchStatus.Ok => "ok",
        SourceSearchStatus.Empty => "empty",
        SourceSearchStatus.Timeout => "timeout",
        SourceSearchStatus.AuthFailed => "auth_failed",
        _ => "error",
    };

    private static List<string> NormalizeQueries(IReadOnlyList<string>? queries, int max)
    {
        var variants = (queries ?? [])
            .Select(q => q?.Trim() ?? string.Empty)
            .Where(q => q.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, max))
            .ToList();

        return variants.Count > 0
            ? variants
            : throw new ConvyRequestException("queries must contain at least one non-empty title variant.");
    }

    /// <summary>
    /// The sources to search, in priority order: the agent's list, else the category's list,
    /// else every active source by name. Missing or disabled sources of the category are
    /// skipped with a warning; those requested by the agent are reported back.
    /// </summary>
    private (List<IContentSource> Selected, List<string> Rejected) SelectSources(
        Category category, IReadOnlyList<string>? requested, IReadOnlyList<IContentSource> all)
    {
        IContentSource? Find(string id) =>
            all.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase) && s.Status != SourceStatus.Disabled);

        var selected = new List<IContentSource>();
        var rejected = new List<string>();

        if (requested is { Count: > 0 })
        {
            foreach (var id in requested.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Find(id.Trim()) is { } source)
                    selected.Add(source);
                else
                    rejected.Add(id.Trim());
            }

            return (selected, rejected);
        }

        if (category.Sources is { Count: > 0 } configured)
        {
            foreach (var id in configured.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Find(id) is { } source)
                    selected.Add(source);
                else
                    _logger.LogWarning("Category '{Category}' lists source '{Source}', which is missing or disabled; skipped.", category.Id, id);
            }

            return (selected, rejected);
        }

        selected.AddRange(all.Where(s => s.Status != SourceStatus.Disabled).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase));
        return (selected, rejected);
    }

    /// <summary>Higher is better: seeders for torrents; free slot, short queue and speed for Soulseek.</summary>
    private static double Rank(Availability a) =>
        a.Seeders
        ?? (a.FreeUploadSlot == true ? 1_000_000 : 0) - (a.QueueLength ?? 0) * 100.0 + (a.UploadSpeed ?? 0) / 1_000_000.0;

    private static IReadOnlyList<string> AddDistinct(IReadOnlyList<string> list, string value) =>
        list.Contains(value, StringComparer.OrdinalIgnoreCase) ? list : [.. list, value];

    private static SearchResultDto ToDto(CachedResult r) => new(
        r.Id,
        r.Sources,
        ProtocolNames.ToName(r.Protocol),
        r.Title,
        r.SizeBytes,
        r.FileCount,
        new AvailabilityDto(r.Availability.Seeders, r.Availability.Leechers, r.Availability.FreeUploadSlot,
            r.Availability.QueueLength, r.Availability.UploadSpeed),
        r.MatchedQueries);

    private sealed record QueryOutcome(
        IContentSource Source, int QueryIndex, IReadOnlyList<ContentInfo> Results, SourceSearchStatus Status, string? Message);

    /// <summary>A result being merged; the first (highest-priority) finding defines it.</summary>
    private sealed class Candidate
    {
        private readonly IContentSource _source;
        private readonly ContentInfo _content;
        private readonly List<string> _sources;
        private readonly List<string> _queries;

        public Candidate(string key, IContentSource source, ContentInfo content, string query, int priority)
        {
            Key = key;
            _source = source;
            _content = content;
            _sources = [source.Id];
            _queries = [query];
            Priority = priority;
            Availability = content.Availability;
        }

        public string Key { get; }
        public int Priority { get; }
        public Availability Availability { get; private set; }

        public void Add(string sourceId, string query, Availability availability)
        {
            if (!_sources.Contains(sourceId, StringComparer.OrdinalIgnoreCase))
                _sources.Add(sourceId);
            if (!_queries.Contains(query, StringComparer.OrdinalIgnoreCase))
                _queries.Add(query);
            if ((availability.Seeders ?? -1) > (Availability.Seeders ?? -1))
                Availability = Availability with { Seeders = availability.Seeders, Leechers = availability.Leechers };
        }

        public CachedResult ToResult(string searchId, DateTimeOffset expiresAt) => new()
        {
            Id = CacheIds.NewResultId(),
            SearchId = searchId,
            Protocol = _source.Protocol,
            Title = _content.Title,
            SizeBytes = _content.SizeBytes,
            FileCount = _content.FileCount,
            Availability = Availability,
            Sources = _sources,
            MatchedQueries = _queries,
            DedupKey = Key,
            SourceId = _source.Id,
            ContentId = _content.ContentId,
            ExpiresAt = expiresAt,
        };
    }
}
