using Convy.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Media;

/// <summary>
/// <c>list_files</c>: obtains a result's file list from its source (a .torrent through the
/// Prowlarr proxy, magnet metadata from the swarm, a Soulseek folder), caches it with the
/// result and renders it page by page.
/// </summary>
public sealed class FileListingService
{
    private readonly ISearchCache _cache;
    private readonly ISourceRegistry _registry;
    private readonly IOptionsMonitor<FilesOptions> _options;
    private readonly ILogger<FileListingService> _logger;

    public FileListingService(
        ISearchCache cache,
        ISourceRegistry registry,
        IOptionsMonitor<FilesOptions> options,
        ILogger<FileListingService> logger)
    {
        _cache = cache;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    public async Task<ListFilesResponse> ListAsync(
        string resultId, string? path, string? glob, int? offset, CancellationToken cancellationToken)
    {
        var result = await GetResultAsync(resultId, cancellationToken).ConfigureAwait(false);
        var listing = await GetListingAsync(result, cancellationToken).ConfigureAwait(false);

        return listing is null
            ? ListFilesResponse.Timeout(result.Id)
            : FileTree.Render(result.Id, listing.Files, path, glob, offset ?? 0, _options.CurrentValue.MaxEntries);
    }

    /// <summary>A cached, unexpired result, or <see cref="ConvyRequestException"/>.</summary>
    public async Task<CachedResult> GetResultAsync(string resultId, CancellationToken cancellationToken) =>
        await _cache.GetResultAsync(resultId, cancellationToken).ConfigureAwait(false)
        ?? throw new ConvyRequestException($"Result '{resultId}' is unknown or expired; search again.");

    /// <summary>
    /// The result's file list from the cache or its source. Returns <c>null</c> when the
    /// source did not deliver it within <see cref="FilesOptions.MetadataTimeoutSeconds"/>;
    /// the attempt is abandoned then, nothing keeps loading in the background.
    /// </summary>
    public async Task<FileListing?> GetListingAsync(CachedResult result, CancellationToken cancellationToken)
    {
        if (await _cache.GetListingAsync(result.Id, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        var source = await _registry.FindAsync(result.SourceId, cancellationToken).ConfigureAwait(false)
                     ?? throw new ConvyRequestException($"Source '{result.SourceId}' of this result is no longer available.");

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.MetadataTimeoutSeconds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        FileListing listing;
        try
        {
            listing = await source.ListFilesAsync(result.ContentId, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("File list of {ResultId} ({Title}) not received within {Timeout} s.", result.Id, result.Title, timeout.TotalSeconds);
            return null;
        }
        catch (SourceException ex)
        {
            throw new ConvyRequestException($"Could not list the files of '{result.Title}': {ex.Message}", ex);
        }

        await _cache.SaveListingAsync(result.Id, listing, result.ExpiresAt, cancellationToken).ConfigureAwait(false);
        return listing;
    }
}
