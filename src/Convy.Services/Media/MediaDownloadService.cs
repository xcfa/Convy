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

    /// <summary>
    /// The <c>download</c> tool: either one result (<paramref name="resultId"/> with its
    /// sub-path and patterns) or several (<paramref name="releases"/>), never both.
    /// </summary>
    public Task<DownloadResponse> DownloadAsync(
        string categoryId,
        string? resultId,
        string? subpath,
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude,
        IReadOnlyList<DownloadRelease>? releases,
        CancellationToken cancellationToken)
    {
        if (releases is { Count: > 0 })
        {
            if (resultId is not null || subpath is not null || include is { Count: > 0 } || exclude is { Count: > 0 })
            {
                throw new ConvyRequestException(
                    "Pass either result_id (with subpath, include, exclude) or releases, not both; each release has its own.");
            }

            return DownloadAsync(categoryId, releases, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(resultId)
            ? throw new ConvyRequestException("Pass result_id, or releases to download several results as one job.")
            : DownloadAsync(resultId, categoryId, subpath, include, exclude, cancellationToken);
    }

    /// <summary>Downloads one result as a job with one release.</summary>
    public Task<DownloadResponse> DownloadAsync(
        string resultId,
        string categoryId,
        string? subpath,
        IReadOnlyList<string>? include,
        IReadOnlyList<string>? exclude,
        CancellationToken cancellationToken) =>
        DownloadAsync(categoryId, [new DownloadRelease(resultId, subpath, include, exclude)], cancellationToken);

    /// <summary>
    /// Downloads several results as one job (e.g. several albums). Each release is prepared
    /// first (result, file selection, payload); a problem with any of them rejects the request.
    /// </summary>
    public async Task<DownloadResponse> DownloadAsync(
        string categoryId, IReadOnlyList<DownloadRelease> releases, CancellationToken cancellationToken)
    {
        if (releases.Count == 0)
        {
            throw new ConvyRequestException("Nothing to download: pass result_id or releases.");
        }

        var category = _catalog.Get(categoryId);
        var requests = new List<StartJobRequest>(releases.Count);
        foreach (var release in releases)
        {
            // With several releases every message says which one it is about.
            var prefix = releases.Count > 1 ? $"{release.ResultId}: " : string.Empty;
            try
            {
                requests.Add(await PrepareAsync(category, release, cancellationToken).ConfigureAwait(false));
            }
            catch (ConvyRequestException ex) when (prefix.Length > 0)
            {
                throw new ConvyRequestException(prefix + ex.Message, ex);
            }
        }

        var started = await _jobs.StartAsync(requests, cancellationToken).ConfigureAwait(false);
        var job = started.Job;
        var many = releases.Count > 1;

        return new DownloadResponse(
            job.JobId,
            job.Status.ToName(),
            started.ExpectedPath,
            started.Rule,
            job.FileCount,
            job.SizeBytes,
            "expected_path is a forecast: rules that depend on changing properties are evaluated again when the files are placed.",
            many
                ? started.Started.Select(s => new DownloadReleaseDto(
                    s.Request.ResultId, s.Request.Title, s.ExpectedPath, s.Rule, s.Release.FileCount, s.Release.SizeBytes)).ToList()
                : null,
            started.Failed.Count > 0
                ? started.Failed.Select(f => new FailedReleaseDto(f.Request.ResultId, f.Request.Title, f.Error)).ToList()
                : null);
    }

    /// <summary>Turns one release of a request into what the job service starts.</summary>
    private async Task<StartJobRequest> PrepareAsync(Category category, DownloadRelease release, CancellationToken cancellationToken)
    {
        if (!SubpathValidator.TryValidate(release.Subpath, out _, out var subpathError))
        {
            throw new ConvyRequestException(subpathError!);
        }

        var result = await _listings.GetResultAsync(release.ResultId, cancellationToken).ConfigureAwait(false);
        var source = await _registry.FindAsync(result.SourceId, cancellationToken).ConfigureAwait(false)
                     ?? throw new ConvyRequestException($"Source '{result.SourceId}' of this result is no longer available.");

        var include = release.Include;
        var exclude = release.Exclude;
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

        return new StartJobRequest
        {
            Payload = payload,
            Selection = selection,
            Category = category.Id,
            ClientCategory = category.ClientCategory,
            Subpath = release.Subpath,
            Title = result.Title,
            SizeBytes = selected?.Sum(f => f.Size) ?? result.SizeBytes,
            FileCount = selected?.Count ?? result.FileCount,
            ResultId = result.Id,
            SourceId = result.SourceId,
        };
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

/// <summary>One result of a <c>download</c> request and how to place it.</summary>
/// <param name="ResultId">Result id from <c>search</c>.</param>
/// <param name="Subpath">Folder replacing the release's root folder, following the category's path_hint.</param>
/// <param name="Include">Globs or exact paths to download; all files when empty.</param>
/// <param name="Exclude">Globs or exact paths to skip, applied after <paramref name="Include"/>.</param>
public sealed record DownloadRelease(
    string ResultId,
    string? Subpath = null,
    IReadOnlyList<string>? Include = null,
    IReadOnlyList<string>? Exclude = null);
