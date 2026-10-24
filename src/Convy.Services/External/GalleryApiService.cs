using Convy.Services.Downloaders.Gallery;
using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Media;
using Convy.Sources;
using Microsoft.Extensions.Options;

namespace Convy.Services.External;

/// <summary>
/// The REST API for external clients that use Convy as their downloader of galleries (the Shizuka provider): the
/// gallery sources with their manifests, search and link resolution through a source, a job per release that goes
/// through the ordinary rules, and the finished CBZ. Everything is behind the API key.
/// </summary>
public sealed class GalleryApiService
{
    private readonly ISourceRegistry _registry;
    private readonly CategoryCatalog _categories;
    private readonly JobService _jobs;
    private readonly IJobStore _jobStore;
    private readonly IDownloaderResolver _downloaders;
    private readonly IOptionsMonitor<GalleryOptions> _options;
    private readonly IOptionsMonitor<SearchOptions> _searchOptions;

    public GalleryApiService(
        ISourceRegistry registry,
        CategoryCatalog categories,
        JobService jobs,
        IJobStore jobStore,
        IDownloaderResolver downloaders,
        IOptionsMonitor<GalleryOptions> options,
        IOptionsMonitor<SearchOptions> searchOptions)
    {
        _registry = registry;
        _categories = categories;
        _jobs = jobs;
        _jobStore = jobStore;
        _downloaders = downloaders;
        _options = options;
        _searchOptions = searchOptions;
    }

    /// <summary>Every gallery source (plugins that implement <see cref="IGallerySource"/>), disabled ones included with their status.</summary>
    public async Task<IReadOnlyList<ApiSourceDto>> ListSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = await _registry.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        return sources.OfType<IGallerySource>().Select(s => new ApiSourceDto(
            s.Id,
            s.Name,
            s.Status.ToString().ToLowerInvariant(),
            s.StatusMessage,
            s.Manifest.Domains,
            s.Manifest.Nsfw,
            s.Manifest.Languages,
            s.Manifest.DefaultAgeRating,
            s.Manifest.CanSearch,
            s.Manifest.Filters.Select(f => new ApiFilterDto(f.Key, f.Name, f.Multiple, f.Options.Select(o => new ApiFilterOptionDto(o.Value, o.Name)).ToList())).ToList()))
            .ToList();
    }

    /// <summary>
    /// A page of releases of a source: by <paramref name="query"/> (empty — the site's front page), with a language,
    /// filters (<c>key=value</c> pairs, repeated for several values) and the page token of the previous answer.
    /// </summary>
    public async Task<ApiSearchDto> SearchAsync(
        string sourceId, string? query, string? language, IReadOnlyDictionary<string, IReadOnlyList<string>> filters, string? page, CancellationToken cancellationToken)
    {
        var source = await FindAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var search = new GallerySearch(Blank(language), filters, Blank(page));
        return await RunAsync(source, async ct =>
        {
            if (source is IGalleryBrowser browser)
            {
                var result = await browser.BrowseAsync(Blank(query), search, ct).ConfigureAwait(false);
                return new ApiSearchDto(result.Items.Select(ApiContentDto.From).ToList(), result.NextPage);
            }

            if (Blank(query) is not { } text)
            {
                throw new ApiException(400, $"{source.Name} needs a query: it does not browse.");
            }

            var items = new List<ApiContentDto>();
            var request = new SearchRequest(text, new CategorySearchSettings(_options.CurrentValue.Category, [], [])) { Gallery = search };
            await foreach (var content in source.SearchAsync(request, ct).ConfigureAwait(false))
            {
                items.Add(ApiContentDto.From(content));
            }

            return new ApiSearchDto(items, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The release a link points to, or 404 when the source does not know it.</summary>
    public async Task<ApiContentDto> ResolveAsync(string sourceId, string url, CancellationToken cancellationToken)
    {
        var source = await FindAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var link) || link.Scheme is not ("http" or "https"))
        {
            throw new ApiException(400, "url must be an absolute http(s) link.");
        }

        var content = await RunAsync(source, ct => source.ResolveUrlAsync(link, ct), cancellationToken).ConfigureAwait(false)
                      ?? throw new ApiException(404, $"{source.Name} has no release at {link}.");
        return ApiContentDto.From(content);
    }

    /// <summary>
    /// Starts a job for a release: the source turns it into a <see cref="GalleryPayload"/>, the job takes the given
    /// category (default <c>gallery.category</c>) and goes through the rules like any other. A job for the same release
    /// that is still running is returned instead of a second one.
    /// </summary>
    public async Task<ApiJobDto> StartJobAsync(ApiStartJobRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceId) || string.IsNullOrWhiteSpace(request.ContentId))
        {
            throw new ApiException(400, "sourceId and contentId are required.");
        }

        var source = await FindAsync(request.SourceId, cancellationToken).ConfigureAwait(false);
        var downloader = Downloader();
        Category category;
        try
        {
            category = _categories.Get(Blank(request.Category) ?? _options.CurrentValue.Category);
        }
        catch (ConvyRequestException ex)
        {
            throw new ApiException(400, ex.Message, ex);
        }

        var payload = await RunAsync(source, ct => source.ResolveAsync(request.ContentId, ct), cancellationToken).ConfigureAwait(false);
        if (payload is not GalleryPayload gallery)
        {
            throw new ApiException(502, $"{source.Name} did not give a gallery for this release.");
        }

        // One job per release: a repeated request (Shizuka retrying) gets the job that is already running.
        if (await _jobStore.FindActiveAsync(downloader.Provider, downloader.GetItemRef(gallery), cancellationToken).ConfigureAwait(false) is { } active)
        {
            return await GetJobAsync(active.JobId, cancellationToken).ConfigureAwait(false)
                   ?? throw new ApiException(500, $"Job {active.JobId} exists but cannot be read.");
        }

        try
        {
            var started = await _jobs.StartAsync(new StartJobRequest
            {
                Payload = gallery,
                Selection = FileSelection.All,
                Category = category.Id,
                ClientCategory = category.ClientCategory,
                Title = gallery.Title,
                FileCount = 1,
                SourceId = source.Id,
                OriginUrl = gallery.InfoUrl,
            }, cancellationToken).ConfigureAwait(false);
            return await GetJobAsync(started.Job.JobId, cancellationToken).ConfigureAwait(false)
                   ?? throw new ApiException(500, "The job was started but cannot be read back.");
        }
        catch (ConvyRequestException ex)
        {
            // The same release in flight already (a race with another client) is a conflict; anything else the job
            // layer refuses (a bad category, nothing to download) is the request's fault.
            throw new ApiException(ex.Message.Contains("same download", StringComparison.Ordinal) || ex.Message.Contains("already", StringComparison.Ordinal) ? 409 : 400, ex.Message, ex);
        }
    }

    /// <summary>The job's state with the gallery's progress; <c>null</c> for an unknown job.</summary>
    public async Task<ApiJobDto?> GetJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var view = await _jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            return null;
        }

        var release = view.Job.Releases[0];
        var download = release.Provider == DownloadProviders.Gallery
            ? await Downloader().FindAsync(release.ItemRef, cancellationToken).ConfigureAwait(false)
            : null;
        var ready = download is { State: GalleryDownloadState.Completed } && File.Exists(download.ArchivePath);
        return new ApiJobDto(
            view.Job.JobId,
            view.Status.ToName(),
            view.Job.Title,
            download?.PagesDone ?? 0,
            download?.PagesTotal,
            ready ? download!.Bytes : null,
            ready,
            view.Error ?? download?.Error);
    }

    /// <summary>The finished archive of a job: its path and name; 404 before it is complete.</summary>
    public async Task<(string Path, string FileName)> GetArchiveAsync(string jobId, CancellationToken cancellationToken)
    {
        var view = await _jobs.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
                   ?? throw new ApiException(404, $"Job {jobId} does not exist.");
        var release = view.Job.Releases[0];
        if (release.Provider != DownloadProviders.Gallery)
        {
            throw new ApiException(404, $"Job {jobId} is not a gallery download.");
        }

        var download = await Downloader().FindAsync(release.ItemRef, cancellationToken).ConfigureAwait(false);
        if (download is not { State: GalleryDownloadState.Completed } || !File.Exists(download.ArchivePath))
        {
            throw new ApiException(404, download?.Error is { } error
                ? $"The archive of job {jobId} is not ready: {error}"
                : $"The archive of job {jobId} is not ready yet ({download?.PagesDone ?? 0}/{download?.PagesTotal?.ToString() ?? "?"} pages).");
        }

        return (download.ArchivePath, download.FileName);
    }

    private async Task<IGallerySource> FindAsync(string sourceId, CancellationToken cancellationToken)
    {
        var source = await _registry.FindAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return source as IGallerySource ?? throw new ApiException(404, $"There is no gallery source '{sourceId}'.");
    }

    private GalleryDownloader Downloader() =>
        _downloaders.FindByProvider(DownloadProviders.Gallery) as GalleryDownloader
        ?? throw new ApiException(503, "Galleries cannot be downloaded: gallery.downloads_path is not set.");

    private async Task<T> RunAsync<T>(IGallerySource source, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var timeout = source.Timeout ?? TimeSpan.FromSeconds(Math.Max(1, _searchOptions.CurrentValue.SourceTimeoutSeconds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await call(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiException(504, $"{source.Name} did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (SourceException ex)
        {
            throw new ApiException(ex.Kind switch { SourceErrorKind.AuthFailed => 401, SourceErrorKind.Challenge => 403, _ => 502 }, $"{source.Name}: {ex.Message}", ex)
            {
                ChallengeUrl = ex.Kind == SourceErrorKind.Challenge ? ex.ChallengeUrl : null,
            };
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
