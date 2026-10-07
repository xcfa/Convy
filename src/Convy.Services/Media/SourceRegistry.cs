using Convy.Services.Webhooks;
using Convy.Sources;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Media;

/// <summary>Gives access to all sources by stable id.</summary>
public interface ISourceRegistry
{
    /// <summary>All sources of all providers, refreshed at most every five minutes.</summary>
    Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken);

    /// <summary>The source with the given id, or <c>null</c>.</summary>
    Task<IContentSource?> FindAsync(string id, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// Asks every <see cref="ISourceProvider"/> and caches the combined list. A provider that
/// fails keeps its last known sources, so a short outage does not make categories lose
/// their sources; the failure itself surfaces when such a source is searched.
/// </remarks>
public sealed class SourceRegistry : ISourceRegistry, IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<ISourceProvider> _providers;
    private readonly ISourceHealth _health;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SourceRegistry> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<IContentSource>> _lastKnown = new(StringComparer.Ordinal);

    // Swapped as a whole so readers outside the lock never see a torn update.
    private volatile Snapshot? _snapshot;

    public SourceRegistry(
        IEnumerable<ISourceProvider> providers, ISourceHealth health, TimeProvider timeProvider, ILogger<SourceRegistry> logger)
    {
        _providers = providers.ToList();
        _health = health;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        if (Fresh(_snapshot) is { } cached)
        {
            return cached;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh(_snapshot) is { } refreshed)
            {
                return refreshed;
            }

            var sources = new List<IContentSource>();
            foreach (var provider in _providers)
            {
                try
                {
                    var provided = await provider.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
                    _lastKnown[provider.Name] = provided;
                    sources.AddRange(provided);

                    foreach (var source in provided.Where(s => s.Status != SourceStatus.Disabled))
                    {
                        _health.Report(source.Id, source.Status == SourceStatus.Ok ? "ok" : "error", source.StatusMessage);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    var previous = _lastKnown.GetValueOrDefault(provider.Name) ?? [];
                    _logger.LogWarning(ex,
                        "Could not list the sources of {Provider}; keeping {Count} known source(s).", provider.Name, previous.Count);
                    sources.AddRange(previous);

                    // Every source of the provider is affected; before any is known, the
                    // provider itself is reported (e.g. a rejected Prowlarr key at startup).
                    var status = ex is SourceException { Kind: SourceErrorKind.AuthFailed } ? "auth_failed" : "error";
                    foreach (var id in previous.Count > 0 ? previous.Select(s => s.Id) : [provider.Name])
                    {
                        _health.Report(id, status, ex.Message);
                    }
                }
            }

            _snapshot = new Snapshot(sources, _timeProvider.GetUtcNow());
            return sources;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<IContentSource?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var sources = await GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        return sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _refreshGate.Dispose();

    private IReadOnlyList<IContentSource>? Fresh(Snapshot? snapshot) =>
        snapshot is not null && _timeProvider.GetUtcNow() - snapshot.RefreshedAt < CacheDuration ? snapshot.Sources : null;

    private sealed record Snapshot(IReadOnlyList<IContentSource> Sources, DateTimeOffset RefreshedAt);
}
