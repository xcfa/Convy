using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Convy.Services.Webhooks;
using Convy.Sources;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Media;

/// <summary>Gives access to all sources by stable id.</summary>
public interface ISourceRegistry
{
    /// <summary>
    /// All sources of all providers. Answers from the last listing without waiting for the
    /// providers; only before the first listing does it wait for one.
    /// </summary>
    Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The last listing, never waiting for one: <c>false</c> until the first listing is done.
    /// For pages that should show what is known rather than wait for the providers.
    /// </summary>
    bool TryGetSources([NotNullWhen(true)] out IReadOnlyList<IContentSource>? sources);

    /// <summary>The source with the given id, or <c>null</c>.</summary>
    Task<IContentSource?> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// The providers that could not list their sources in the last refresh; their last known
    /// sources are still served by <see cref="GetSourcesAsync"/>.
    /// </summary>
    IReadOnlyList<SourceProviderError> ProviderErrors { get; }
}

/// <summary>A provider that could not list its sources.</summary>
/// <param name="Provider">The provider's name (<c>prowlarr</c>, <c>slskd</c>, …).</param>
/// <param name="Message">Why.</param>
/// <param name="KnownSources">How many of its sources are still served from the last listing.</param>
public sealed record SourceProviderError(string Provider, string Message, int KnownSources);

/// <inheritdoc />
/// <remarks>
/// Asks every <see cref="ISourceProvider"/> (all at once) and keeps the combined list. A
/// listing older than five minutes is still served while <see cref="RunAsync"/> makes a new
/// one in the background: listing asks Prowlarr, slskd and every OPDS catalog, which can take
/// long (Flibusta), and neither a page of the UI nor a search should wait for it.
/// A provider that fails keeps its last known sources, so a short outage does not make
/// categories lose their sources; the failure itself surfaces when such a source is searched.
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

    // One pending request is enough: the next listing serves everyone who asked.
    private readonly Channel<bool> _refreshRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

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

    public async Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken) =>
        TryGetSources(out var sources)
            ? sources
            : (await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false)).Sources;

    public bool TryGetSources([NotNullWhen(true)] out IReadOnlyList<IContentSource>? sources)
    {
        if (_snapshot is not { } snapshot)
        {
            sources = null;
            return false;
        }

        if (!IsFresh(snapshot))
        {
            _refreshRequests.Writer.TryWrite(true);
        }

        sources = snapshot.Sources;
        return true;
    }

    public async Task<IContentSource?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var sources = await GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        return sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<SourceProviderError> ProviderErrors => _snapshot?.Errors ?? [];

    /// <summary>
    /// Lists the sources right away, then again whenever a caller finds the listing older than
    /// five minutes. Runs until <paramref name="stoppingToken"/> is cancelled; meant for a hosted service.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshLoggedAsync(stoppingToken).ConfigureAwait(false);
            await foreach (var _ in _refreshRequests.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshLoggedAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Lists the sources of every provider, unless the last listing is still fresh.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken) => RefreshSnapshotAsync(cancellationToken);

    public void Dispose() => _refreshGate.Dispose();

    private async Task RefreshLoggedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshSnapshotAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not list the sources; the previous list stays in use.");
        }
    }

    private async Task<Snapshot> RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Someone else may have listed them while this caller waited.
            if (_snapshot is { } current && IsFresh(current))
            {
                return current;
            }

            var listings = await Task.WhenAll(_providers.Select(p => ListAsync(p, cancellationToken))).ConfigureAwait(false);

            var sources = new List<IContentSource>();
            var errors = new List<SourceProviderError>();
            foreach (var listing in listings)
            {
                var provider = listing.Provider;
                if (listing.Failure is null)
                {
                    _lastKnown[provider.Name] = listing.Sources;
                    sources.AddRange(listing.Sources);

                    foreach (var source in listing.Sources.Where(s => s.Status != SourceStatus.Disabled))
                    {
                        _health.Report(source.Id, source.Status == SourceStatus.Ok ? "ok" : "error", source.StatusMessage);
                    }

                    continue;
                }

                var failure = listing.Failure;
                var previous = _lastKnown.GetValueOrDefault(provider.Name) ?? [];
                _logger.LogWarning(failure,
                    "Could not list the sources of {Provider}; keeping {Count} known source(s).", provider.Name, previous.Count);
                sources.AddRange(previous);
                errors.Add(new SourceProviderError(provider.Name, failure.Message, previous.Count));

                // Every source of the provider is affected; before any is known, the
                // provider itself is reported (e.g. a rejected Prowlarr key at startup).
                var status = failure is SourceException { Kind: SourceErrorKind.AuthFailed or SourceErrorKind.Challenge } ? "auth_failed" : "error";
                foreach (var id in previous.Count > 0 ? previous.Select(s => s.Id) : [provider.Name])
                {
                    _health.Report(id, status, failure.Message);
                }
            }

            var snapshot = new Snapshot(sources, errors, _timeProvider.GetUtcNow());
            _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>One provider's sources, or why it could not list them.</summary>
    private static async Task<Listing> ListAsync(ISourceProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return new Listing(provider, await provider.GetSourcesAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new Listing(provider, [], ex);
        }
    }

    private bool IsFresh(Snapshot snapshot) => _timeProvider.GetUtcNow() - snapshot.RefreshedAt < CacheDuration;

    private sealed record Listing(ISourceProvider Provider, IReadOnlyList<IContentSource> Sources, Exception? Failure);

    private sealed record Snapshot(IReadOnlyList<IContentSource> Sources, IReadOnlyList<SourceProviderError> Errors, DateTimeOffset RefreshedAt);
}
