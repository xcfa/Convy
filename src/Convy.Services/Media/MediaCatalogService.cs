using Convy.Services.Jobs;
using Convy.Sources;

namespace Convy.Services.Media;

/// <summary>
/// The small read/act tools: <c>get_categories</c>, <c>get_sources</c>, <c>get_jobs</c>,
/// <c>cancel_job</c>. Maps the services' models to the agent-facing responses.
/// </summary>
public sealed class MediaCatalogService
{
    private readonly CategoryCatalog _catalog;
    private readonly ISourceRegistry _registry;
    private readonly JobService _jobs;

    public MediaCatalogService(CategoryCatalog catalog, ISourceRegistry registry, JobService jobs)
    {
        _catalog = catalog;
        _registry = registry;
        _jobs = jobs;
    }

    /// <summary>Categories with the sources each one searches, in priority order.</summary>
    public async Task<CategoriesResponse> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        var sources = await _registry.GetSourcesAsync(cancellationToken).ConfigureAwait(false);

        var categories = _catalog.All
            .Select(c => new CategoryDto(c.Id, c.Description, c.PathHint,
                CategorySources.Resolve(c, sources, out _).Select(s => s.Id).ToList()))
            .ToList();

        return new CategoriesResponse(categories);
    }

    public async Task<SourcesResponse> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = await _registry.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        return new SourcesResponse(ToDtos(sources));
    }

    /// <summary>The sources as last listed, without waiting for a listing; <c>null</c> before the first one.</summary>
    public IReadOnlyList<SourceDto>? TryGetSources() =>
        _registry.TryGetSources(out var sources) ? ToDtos(sources) : null;

    private static List<SourceDto> ToDtos(IReadOnlyList<IContentSource> sources) => sources
        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .Select(s => new SourceDto(s.Id, s.Name, ProtocolNames.ToName(s.Protocol), SourceStatusNames.ToName(s.Status), s.StatusMessage))
        .ToList();

    public async Task<JobsResponse> GetJobsAsync(string? status, int? limit, CancellationToken cancellationToken)
    {
        JobStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            filter = JobStatusNames.TryParse(status, out var parsed)
                ? parsed
                : throw new ConvyRequestException(
                    $"Unknown status '{status}'. Use one of: {string.Join(", ", Enum.GetValues<JobStatus>().Select(s => s.ToName()))}.");
        }

        var views = await _jobs.ListAsync(filter, limit, cancellationToken).ConfigureAwait(false);
        return new JobsResponse(views.Select(ToDto).ToList());
    }

    public async Task<CancelJobResponse> CancelJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await _jobs.CancelAsync(jobId, cancellationToken).ConfigureAwait(false);
        return new CancelJobResponse(job.JobId, job.Status.ToName(), "The download was stopped; downloaded data and created links are kept.");
    }

    private static JobDto ToDto(JobView view) => new(
        view.Job.JobId,
        view.Status.ToName(),
        view.Job.Title,
        view.Job.Category,
        view.Job.Provider,
        view.Progress,
        view.DownloadedBytes,
        view.Job.SizeBytes,
        view.SpeedBytesPerSecond,
        view.Job.TargetPath,
        view.Job.Rule,
        view.Error,
        view.Job.CreatedAt,
        view.Releases.Count > 1
            ? view.Releases.Select(r => new JobReleaseDto(
                r.Release.Title,
                r.Status.ToName(),
                r.Release.Provider,
                r.Progress,
                r.TotalBytes ?? r.Release.SizeBytes,
                r.Release.TargetPath,
                r.Release.Rule,
                r.Error)).ToList()
            : null);
}
