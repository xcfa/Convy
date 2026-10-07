using Convy.Infrastructure.Helpers;
using Convy.Services.Downloads;
using Convy.Services.Placement;
using Convy.Services.Rules;
using Convy.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Jobs;

/// <summary>What to download and how to place it.</summary>
public sealed record StartJobRequest
{
    public required DownloadPayload Payload { get; init; }
    public required FileSelection Selection { get; init; }

    /// <summary>Category id from the configuration.</summary>
    public required string Category { get; init; }

    /// <summary>Client category (<c>qbittorrent_category</c>); the rules see it as <c>Category</c>.</summary>
    public string? ClientCategory { get; init; }

    /// <summary>Relative directory replacing the download's root folder; validated here.</summary>
    public string? Subpath { get; init; }

    public required string Title { get; init; }

    /// <summary>Size of the selected files in bytes, when known.</summary>
    public long? SizeBytes { get; init; }

    public int? FileCount { get; init; }
    public string? ResultId { get; init; }
    public string? SourceId { get; init; }
}

/// <summary>A started job with the placement expected at the moment of starting.</summary>
/// <param name="Job">The new job.</param>
/// <param name="ExpectedPath">
/// Where the files are expected to end up. A forecast: rules that depend on changing
/// properties (<c>Ratio</c>, <c>SeedingTime</c>) are evaluated again on placement.
/// </param>
/// <param name="Rule">Name of the rule expected to place the files, or <c>null</c>.</param>
public sealed record StartJobResult(JobRecord Job, string? ExpectedPath, string? Rule);

/// <summary>A job with its live state.</summary>
public sealed record JobView
{
    public required JobRecord Job { get; init; }

    /// <summary>Status derived from the downloader right now (stored status for finished jobs).</summary>
    public required JobStatus Status { get; init; }

    /// <summary>Completion between 0 and 1, when known.</summary>
    public double? Progress { get; init; }

    public long? DownloadedBytes { get; init; }
    public long? SpeedBytesPerSecond { get; init; }
    public string? Error { get; init; }
}

/// <summary>Starts, lists and cancels jobs. All MCP job tools delegate here.</summary>
public sealed class JobService
{
    private const long GiB = 1024L * 1024 * 1024;
    private const int MaxListLimit = 100;

    private readonly IDownloaderResolver _downloaders;
    private readonly IRulesProvider _rulesProvider;
    private readonly IJobStore _store;
    private readonly JobTransitions _transitions;
    private readonly IFileSystemInspector _fileSystem;
    private readonly IOptionsMonitor<JobOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobService> _logger;

    public JobService(
        IDownloaderResolver downloaders,
        IRulesProvider rulesProvider,
        IJobStore store,
        JobTransitions transitions,
        IFileSystemInspector fileSystem,
        IOptionsMonitor<JobOptions> options,
        TimeProvider timeProvider,
        ILogger<JobService> logger)
    {
        _downloaders = downloaders;
        _rulesProvider = rulesProvider;
        _store = store;
        _transitions = transitions;
        _fileSystem = fileSystem;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Validates the request, adds the download to its downloader right away and records the
    /// job. Throws <see cref="ConvyRequestException"/> when the request cannot be accepted.
    /// </summary>
    public async Task<StartJobResult> StartAsync(StartJobRequest request, CancellationToken cancellationToken)
    {
        if (!SubpathValidator.TryValidate(request.Subpath, out var subpath, out var subpathError))
        {
            throw new ConvyRequestException(subpathError!);
        }

        var downloader = ResolveDownloader(request.Payload.Protocol);
        var options = new AddOptions(request.ClientCategory);

        // One job per download: a repeated request (e.g. after a client timeout) must not
        // create a second job that would never be placed.
        var itemRef = downloader.GetItemRef(request.Payload);
        if (await _store.FindActiveAsync(downloader.Provider, itemRef, cancellationToken).ConfigureAwait(false) is { } active)
        {
            throw new ConvyRequestException(
                $"This download is already job {active.JobId} ({active.Status.ToName()}); check it with get_jobs or cancel it first.");
        }
        var limits = _options.CurrentValue;

        if (limits.MaxSizeGb > 0 && request.SizeBytes > limits.MaxSizeGb * GiB)
        {
            throw new ConvyRequestException(
                $"The download is {FormatGiB(request.SizeBytes.Value)}, above the {limits.MaxSizeGb:0.##} GiB limit per job.");
        }

        var rules = _rulesProvider.GetCurrent();
        var downloadDirectory = await TryGetDownloadDirectoryAsync(downloader, options, cancellationToken).ConfigureAwait(false);

        // Check the sub-path against the expected base before anything is downloaded.
        var preliminary = BasicProperties(downloader.Provider, request);
        if (subpath is not null && downloadDirectory is not null)
        {
            var target = PlacementPlanner.ResolveTarget(rules.ResolveRule(preliminary), subpath, downloadDirectory);
            if (!SubpathValidator.IsInside(_fileSystem.GetRealPath(target.BaseDirectory!), _fileSystem.GetRealPath(target.Directory!)))
            {
                throw new ConvyRequestException($"subpath resolves outside '{target.BaseDirectory}'.");
            }
        }

        EnsureFreeSpace(downloadDirectory, request.SizeBytes, limits);

        itemRef = await downloader.AddAsync(request.Payload, request.Selection, options, cancellationToken).ConfigureAwait(false);

        // Forecast the placement with the item as the downloader reports it now, so the job
        // (and its first status event) already carries the expected rule and path.
        var item = await TryGetItemAsync(downloader, itemRef, cancellationToken).ConfigureAwait(false);
        var properties = item is null ? preliminary : RuleInputs.For(item, request.ClientCategory);
        var savePath = item?.SavePath is { Length: > 0 } itemPath ? itemPath : downloadDirectory;
        var rule = rules.ResolveRule(properties);
        var expected = PlacementPlanner.ResolveTarget(rule, subpath, savePath ?? string.Empty);
        var expectedPath = expected.LeaveInPlace ? savePath : expected.Directory;
        var now = _timeProvider.GetUtcNow();

        var job = await _transitions.CreateAsync(new JobRecord
        {
            Id = 0,
            Provider = downloader.Provider,
            ItemRef = itemRef,
            Category = request.Category,
            ClientCategory = request.ClientCategory,
            Subpath = subpath,
            SelectedFiles = request.Selection.Paths?.ToList(),
            Title = request.Title,
            SizeBytes = request.SizeBytes,
            FileCount = request.FileCount,
            ResultId = request.ResultId,
            SourceId = request.SourceId,
            Status = JobStatus.Queued,
            Rule = rule?.Name,
            TargetPath = expectedPath,
            LastProgressAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Job {JobId} started: {Title} via {Provider} ({ItemRef}).", job.JobId, job.Title, job.Provider, itemRef);

        return new StartJobResult(job, expectedPath, rule?.Name);
    }

    /// <summary>
    /// Stops the job's download (data and created links are kept) and marks it cancelled.
    /// </summary>
    public async Task<JobRecord> CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await GetRequiredAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.Status.IsTerminal())
        {
            throw new ConvyRequestException($"Job {job.JobId} is already {job.Status.ToName()}.");
        }

        var downloader = _downloaders.FindByProvider(job.Provider)
                         ?? throw new ConvyRequestException($"Downloader '{job.Provider}' is not configured.");

        // Record the cancellation first: once the job is final, the sync worker cannot turn
        // the stopped download into "failed" in between. It may update the job concurrently
        // before that, so retry on its fresh state.
        JobRecord? cancelled = null;
        for (var attempt = 0; attempt < 5 && cancelled is null; attempt++)
        {
            var now = _timeProvider.GetUtcNow();
            cancelled = await _transitions.SaveAsync(
                job, job with { Status = JobStatus.Cancelled, UpdatedAt = now, CompletedAt = now }, cancellationToken)
                .ConfigureAwait(false);

            if (cancelled is null)
            {
                job = await GetRequiredAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (job.Status.IsTerminal())
                {
                    throw new ConvyRequestException($"Job {job.JobId} is already {job.Status.ToName()}.");
                }
            }
        }

        if (cancelled is null)
        {
            throw new InvalidOperationException($"Job {job.JobId} kept changing; cancellation was not recorded.");
        }

        try
        {
            await downloader.CancelAsync(job.ItemRef, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The download keeps running, so the job is not cancelled after all.
            await _transitions.SaveAsync(cancelled, job, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await _transitions.PublishAsync(new JobStatusChange(cancelled, job.Status), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Job {JobId} cancelled.", cancelled.JobId);
        return cancelled;
    }

    /// <summary>
    /// Newest jobs first with their live state. Active jobs are looked up in their
    /// downloader directly, so the result does not wait for a sync cycle.
    /// </summary>
    public async Task<IReadOnlyList<JobView>> ListAsync(JobStatus? status, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 20, 1, MaxListLimit);

        // A finished job's status never changes, so it can be filtered in the store; an
        // active job's live status may differ from the stored one.
        var jobs = status is { } filter && filter.IsTerminal()
            ? await _store.ListAsync(filter, take, cancellationToken).ConfigureAwait(false)
            : await _store.ListAsync(null, status is null ? take : 500, cancellationToken).ConfigureAwait(false);

        var views = new List<JobView>(jobs.Count);
        foreach (var job in jobs)
        {
            var view = await ViewAsync(job, cancellationToken).ConfigureAwait(false);
            if (status is null || view.Status == status)
            {
                views.Add(view);
                if (views.Count == take)
                {
                    break;
                }
            }
        }

        return views;
    }

    private async Task<JobView> ViewAsync(JobRecord job, CancellationToken cancellationToken)
    {
        if (job.Status.IsTerminal())
        {
            return new JobView
            {
                Job = job,
                Status = job.Status,
                Progress = job.Status == JobStatus.Completed ? 1 : null,
                Error = job.Error,
            };
        }

        var downloader = _downloaders.FindByProvider(job.Provider);
        if (downloader is null)
        {
            return new JobView { Job = job, Status = job.Status, Error = $"Downloader '{job.Provider}' is not configured." };
        }

        DownloadItem? item;
        try
        {
            item = await downloader.GetItemAsync(job.ItemRef, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read job {JobId} from {Provider}.", job.JobId, job.Provider);
            return new JobView { Job = job, Status = job.Status, Error = $"{job.Provider} is unreachable: {ex.Message}" };
        }

        var observation = JobStatusEvaluator.Observe(
            job, item, _timeProvider.GetUtcNow(), _options.CurrentValue.StalledAfter);

        return new JobView
        {
            Job = job,
            Status = observation.Status,
            Progress = item?.Size is > 0 ? Math.Clamp((double)item.Downloaded / item.Size.Value, 0, 1) : null,
            DownloadedBytes = item?.Downloaded,
            SpeedBytesPerSecond = item?.DownloadSpeed,
            Error = observation.Error ?? job.Error,
        };
    }

    private async Task<JobRecord> GetRequiredAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!JobIds.TryParse(jobId, out var id))
        {
            throw new ConvyRequestException($"'{jobId}' is not a job id (expected e.g. j_42).");
        }

        return await _store.GetAsync(id, cancellationToken).ConfigureAwait(false)
               ?? throw new ConvyRequestException($"Job {jobId} does not exist.");
    }

    private IDownloader ResolveDownloader(Protocol protocol)
    {
        try
        {
            return _downloaders.Resolve(protocol);
        }
        catch (InvalidOperationException ex)
        {
            throw new ConvyRequestException(ex.Message, ex);
        }
    }

    private void EnsureFreeSpace(string? downloadDirectory, long? size, JobOptions limits)
    {
        if (downloadDirectory is null || size is null)
        {
            return;
        }

        var free = _fileSystem.GetAvailableFreeSpace(downloadDirectory);
        var reserve = (long)(limits.MinFreeSpaceGb * GiB);
        if (free is not null && size.Value + reserve > free.Value)
        {
            throw new ConvyRequestException(
                $"Not enough free space in '{downloadDirectory}': {FormatGiB(free.Value)} free, " +
                $"{FormatGiB(size.Value)} needed" + (reserve > 0 ? $" plus a {FormatGiB(reserve)} reserve." : "."));
        }
    }

    private async Task<string?> TryGetDownloadDirectoryAsync(IDownloader downloader, AddOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await downloader.GetDownloadDirectoryAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the download directory of {Provider}.", downloader.Provider);
            return null;
        }
    }

    private async Task<DownloadItem?> TryGetItemAsync(IDownloader downloader, string itemRef, CancellationToken cancellationToken)
    {
        try
        {
            return await downloader.GetItemAsync(itemRef, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read {Provider} item {ItemRef} after adding it.", downloader.Provider, itemRef);
            return null;
        }
    }

    /// <summary>The properties known before the downloader has the item.</summary>
    private static Dictionary<string, object?> BasicProperties(string provider, StartJobRequest request)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Provider"] = provider,
            ["Name"] = request.Title,
            ["Category"] = request.ClientCategory,
        };

        if (request.SizeBytes is { } size)
        {
            properties["Size"] = (double)size;
        }

        return properties;
    }

    private static string FormatGiB(long bytes) => $"{bytes / (double)GiB:0.##} GiB";
}
