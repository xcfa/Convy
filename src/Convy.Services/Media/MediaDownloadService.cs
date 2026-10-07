using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Placement;
using Convy.Sources;
using Microsoft.Extensions.Options;

namespace Convy.Services.Media;

/// <summary>
/// <c>download</c>: validates the request, applies the file selection, lets the result's
/// source produce the payload and starts a job. Placement happens later in the sync worker.
/// </summary>
public sealed class MediaDownloadService
{
    private readonly CategoryCatalog _catalog;
    private readonly ISourceRegistry _registry;
    private readonly ISearchCache _cache;
    private readonly FileListingService _listings;
    private readonly JobService _jobs;
    private readonly IOptionsMonitor<SearchOptions> _searchOptions;

    public MediaDownloadService(
        CategoryCatalog catalog,
        ISourceRegistry registry,
        ISearchCache cache,
        FileListingService listings,
        JobService jobs,
        IOptionsMonitor<SearchOptions> searchOptions)
    {
        _catalog = catalog;
        _registry = registry;
        _cache = cache;
        _listings = listings;
        _jobs = jobs;
        _searchOptions = searchOptions;
    }

    public async Task<DownloadResponse> DownloadAsync(
        string resultId,
        string categoryId,
        string? subpath,
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude,
        CancellationToken cancellationToken)
    {
        var category = _catalog.Get(categoryId);
        if (!SubpathValidator.TryValidate(subpath, out _, out var subpathError))
        {
            throw new ConvyRequestException(subpathError!);
        }

        var result = await _listings.GetResultAsync(resultId, cancellationToken).ConfigureAwait(false);
        var source = await _registry.FindAsync(result.SourceId, cancellationToken).ConfigureAwait(false)
                     ?? throw new ConvyRequestException($"Source '{result.SourceId}' of this result is no longer available.");

        var hasPatterns = include is { Count: > 0 } || exclude is { Count: > 0 };
        var listing = hasPatterns
            ? await _listings.GetListingAsync(result, cancellationToken).ConfigureAwait(false)
              ?? throw new ConvyRequestException(
                  "The file list of this result could not be obtained in time, so include/exclude cannot be applied. " +
                  "Download it without include/exclude to get every file, or pick another result.")
            : await _cache.GetListingAsync(result.Id, cancellationToken).ConfigureAwait(false);

        var selected = hasPatterns ? FileSelector.Select(listing!.Files, include, exclude) : listing?.Files;
        var selection = !hasPatterns || selected!.Count == listing!.Files.Count
            ? FileSelection.All
            : new FileSelection(selected.Select(f => f.Path).ToList());

        var payload = await ResolveAsync(source, result, cancellationToken).ConfigureAwait(false);
        if (!selection.IsAll && payload is TorrentPayload { TorrentFile: null } torrent && listing?.TorrentFile is { } metadata)
        {
            // The listing came from the swarm: hand the metadata over so the client can
            // apply the selection before fetching anything.
            payload = torrent with { TorrentFile = metadata };
        }

        var started = await _jobs.StartAsync(new StartJobRequest
        {
            Payload = payload,
            Selection = selection,
            Category = category.Id,
            ClientCategory = category.ClientCategory,
            Subpath = subpath,
            Title = result.Title,
            SizeBytes = selected?.Sum(f => f.Size) ?? result.SizeBytes,
            FileCount = selected?.Count ?? result.FileCount,
            ResultId = result.Id,
            SourceId = result.SourceId,
        }, cancellationToken).ConfigureAwait(false);

        return new DownloadResponse(
            started.Job.JobId,
            started.Job.Status.ToName(),
            started.ExpectedPath,
            started.Rule,
            started.Job.FileCount,
            started.Job.SizeBytes,
            "expected_path is a forecast: rules that depend on changing properties are evaluated again when the files are placed.");
    }

    private async Task<DownloadPayload> ResolveAsync(IContentSource source, CachedResult result, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _searchOptions.CurrentValue.SourceTimeoutSeconds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            return await source.ResolveAsync(result.ContentId, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConvyRequestException($"{source.Name} did not provide the download within {timeout.TotalSeconds:0} s.");
        }
        catch (SourceException ex)
        {
            throw new ConvyRequestException($"{source.Name} could not provide the download: {ex.Message}", ex);
        }
    }
}
