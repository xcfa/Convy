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

/// <summary>A release that was added to its downloader, with the placement expected at that moment.</summary>
/// <param name="Request">What was asked for.</param>
/// <param name="Release">The stored release.</param>
/// <param name="ExpectedPath">
/// Where the files are expected to end up. A forecast: rules that depend on changing
/// properties (<c>Ratio</c>, <c>SeedingTime</c>) are evaluated again on placement.
/// </param>
/// <param name="Rule">Name of the rule expected to place the files, or <c>null</c>.</param>
public sealed record StartedRelease(StartJobRequest Request, JobRecord Release, string? ExpectedPath, string? Rule);

/// <summary>A release its downloader did not accept; the job was started without it.</summary>
public sealed record FailedRelease(StartJobRequest Request, string Error);

/// <summary>A started job: the releases that were added and those that were not.</summary>
public sealed record StartJobResult(JobState Job, IReadOnlyList<StartedRelease> Started, IReadOnlyList<FailedRelease> Failed)
{
    /// <summary>The expected directory: the release's, or the one containing every release's.</summary>
    public string? ExpectedPath => Started.Count == 1
        ? Started[0].ExpectedPath
        : JobState.CommonDirectory(Started.Select(s => s.ExpectedPath));

    /// <summary>The rule expected for every release, or <c>null</c> when they differ.</summary>
    public string? Rule => Started.Select(s => s.Rule).Distinct(StringComparer.Ordinal).Count() == 1 ? Started[0].Rule : null;
}

/// <summary>A job with its live state, combined from its releases.</summary>
public sealed record JobView
{
    public required JobState Job { get; init; }

    /// <summary>Status derived from the downloaders right now (stored status for finished releases).</summary>
    public required JobStatus Status { get; init; }

    /// <summary>Completion between 0 and 1, when known.</summary>
    public double? Progress { get; init; }

    public long? DownloadedBytes { get; init; }
    public long? SpeedBytesPerSecond { get; init; }
    public string? Error { get; init; }

    /// <summary>Each release with its live state, in the job's order.</summary>
    public required IReadOnlyList<ReleaseView> Releases { get; init; }
}

/// <summary>One release of a job with its live state.</summary>
public sealed record ReleaseView
{
    public required JobRecord Release { get; init; }
    public required JobStatus Status { get; init; }
    public double? Progress { get; init; }

    /// <summary>Size reported by the downloader, when it knows the item.</summary>
    public long? TotalBytes { get; init; }

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
    /// Validates the request, adds the download to its downloader right away and records a job
    /// with one release. Throws <see cref="ConvyRequestException"/> when it cannot be accepted.
    /// </summary>
    public Task<StartJobResult> StartAsync(StartJobRequest request, CancellationToken cancellationToken) =>
        StartAsync([request], cancellationToken);

    /// <summary>
    /// Starts one job made of several releases. Every release is checked first (sub-path,
    /// duplicates, size limit, free space); a problem with any of them rejects the whole
    /// request before anything is added. Releases are then added to their downloaders one by
    /// one: those a downloader refuses are reported in <see cref="StartJobResult.Failed"/> and
    /// the job is made of the rest. Throws when none could be added.
    /// </summary>
    public async Task<StartJobResult> StartAsync(IReadOnlyList<StartJobRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            throw new ConvyRequestException("Nothing to download.");
        }

        var limits = _options.CurrentValue;
        var rules = _rulesProvider.GetCurrent();
        var directories = new Dictionary<(string Provider, string? Category), string?>();
        var prepared = new List<PreparedRelease>(requests.Count);

        foreach (var request in requests)
        {
            prepared.Add(await PrepareAsync(request, requests.Count, prepared, rules, directories, cancellationToken)
                .ConfigureAwait(false));
        }

        var total = requests.Any(r => r.SizeBytes is not null) ? requests.Sum(r => r.SizeBytes ?? 0) : (long?)null;
        if (limits.MaxSizeGb > 0 && total > limits.MaxSizeGb * GiB)
        {
            throw new ConvyRequestException(
                $"The download is {FormatGiB(total.Value)}, above the {limits.MaxSizeGb:0.##} GiB limit per job.");
        }

        foreach (var directory in prepared.Where(p => p.DownloadDirectory is not null).GroupBy(p => p.DownloadDirectory!))
        {
            var size = directory.Any(p => p.Request.SizeBytes is not null) ? directory.Sum(p => p.Request.SizeBytes ?? 0) : (long?)null;
            EnsureFreeSpace(directory.Key, size, limits);
        }

        var started = new List<(PreparedRelease Release, JobRecord Record, string? ExpectedPath, string? Rule)>();
        var failed = new List<FailedRelease>();
        Exception? firstFailure = null;

        foreach (var release in prepared)
        {
            try
            {
                started.Add(await AddAsync(release, rules, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && requests.Count > 1)
            {
                _logger.LogWarning(ex, "{Provider} did not accept '{Title}'.", release.Downloader.Provider, release.Request.Title);
                failed.Add(new FailedRelease(release.Request, ex.Message));
                firstFailure ??= ex;
            }
        }

        if (started.Count == 0)
        {
            throw new ConvyRequestException(
                "No release could be added: " + string.Join("; ", failed.Select(f => $"{f.Request.Title}: {f.Error}")), firstFailure!);
        }

        var job = await _transitions.CreateGroupAsync(started.Select(s => s.Record).ToList(), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Job {JobId} started: {Title} ({Count} release(s)).", job.JobId, job.Title, job.Releases.Count);

        return new StartJobResult(
            job,
            started.Select((s, i) => new StartedRelease(s.Release.Request, job.Releases[i], s.ExpectedPath, s.Rule)).ToList(),
            failed);
    }

    /// <summary>
    /// Stops the downloads of the job's unfinished releases (data and created links are kept)
    /// and marks them cancelled.
    /// </summary>
    public async Task<JobState> CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await GetRequiredAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.Status.IsTerminal())
        {
            throw new ConvyRequestException($"Job {job.JobId} is already {job.Status.ToName()}.");
        }

        try
        {
            foreach (var release in job.Releases.Where(r => !r.Status.IsTerminal()))
            {
                await CancelReleaseAsync(release, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Announce what was cancelled, even if a later release could not be stopped.
            var after = JobState.From(await _store.GetGroupAsync(job.Id, CancellationToken.None).ConfigureAwait(false));
            if (after.Status != job.Status)
            {
                await _transitions.PublishAsync(new JobStatusChange(after, job.Status), CancellationToken.None).ConfigureAwait(false);
            }
        }

        var cancelled = JobState.From(await _store.GetGroupAsync(job.Id, cancellationToken).ConfigureAwait(false));
        _logger.LogInformation("Job {JobId} cancelled.", cancelled.JobId);
        return cancelled;
    }

    /// <summary>
    /// Newest jobs first with their live state. Active releases are looked up in their
    /// downloader directly, so the result does not wait for a sync cycle.
    /// </summary>
    public async Task<IReadOnlyList<JobView>> ListAsync(JobStatus? status, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 20, 1, MaxListLimit);

        // A job's live status may differ from the stored one, so filtering happens after the
        // downloaders were asked.
        var jobs = await _store.ListGroupsAsync(status is null ? take : 500, cancellationToken).ConfigureAwait(false);

        // A downloader that failed once is not asked again for the other jobs of this list:
        // with retries each attempt can take seconds.
        var unreachable = new Dictionary<string, string>(StringComparer.Ordinal);
        var views = new List<JobView>(jobs.Count);
        foreach (var releases in jobs.Where(r => r.Count > 0))
        {
            var view = await ViewAsync(JobState.From(releases), unreachable, cancellationToken).ConfigureAwait(false);
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

    private async Task<JobView> ViewAsync(JobState job, Dictionary<string, string> unreachable, CancellationToken cancellationToken)
    {
        var releases = new List<ReleaseView>(job.Releases.Count);
        foreach (var release in job.Releases)
        {
            releases.Add(await ViewReleaseAsync(release, unreachable, cancellationToken).ConfigureAwait(false));
        }

        if (releases.Count == 1)
        {
            var only = releases[0];
            return new JobView
            {
                Job = job,
                Status = only.Status,
                Progress = only.Progress,
                DownloadedBytes = only.DownloadedBytes,
                SpeedBytesPerSecond = only.SpeedBytesPerSecond,
                Error = only.Error,
                Releases = releases,
            };
        }

        // Progress over all releases: a release past downloading counts as fully downloaded.
        var sizes = releases.Select(r => r.TotalBytes ?? r.Release.SizeBytes).ToList();
        var done = releases
            .Select((r, i) => r.DownloadedBytes ?? (r.Status is JobStatus.Completed or JobStatus.Placing ? sizes[i] ?? 0 : 0))
            .ToList();
        double? progress = sizes.All(s => s is not null) && sizes.Sum(s => s!.Value) > 0
            ? Math.Clamp((double)done.Sum() / sizes.Sum(s => s!.Value), 0, 1)
            : null;

        return new JobView
        {
            Job = job,
            Status = JobState.Combine(releases.Select(r => r.Status)),
            Progress = progress,
            DownloadedBytes = releases.Any(r => r.DownloadedBytes is not null) ? releases.Sum(r => r.DownloadedBytes ?? 0) : null,
            SpeedBytesPerSecond = releases.Any(r => r.SpeedBytesPerSecond is not null)
                ? releases.Sum(r => r.SpeedBytesPerSecond ?? 0)
                : null,
            Error = JobState.CombineErrors(releases.Select(r => (r.Release.Title, r.Error))),
            Releases = releases,
        };
    }

    private async Task<ReleaseView> ViewReleaseAsync(
        JobRecord release, Dictionary<string, string> unreachable, CancellationToken cancellationToken)
    {
        if (release.Status.IsTerminal())
        {
            return new ReleaseView
            {
                Release = release,
                Status = release.Status,
                Progress = release.Status == JobStatus.Completed ? 1 : null,
                Error = release.Error,
            };
        }

        var downloader = _downloaders.FindByProvider(release.Provider);
        if (downloader is null)
        {
            return new ReleaseView { Release = release, Status = release.Status, Error = $"Downloader '{release.Provider}' is not configured." };
        }

        if (unreachable.GetValueOrDefault(release.Provider) is { } knownError)
        {
            return new ReleaseView { Release = release, Status = release.Status, Error = knownError };
        }

        DownloadItem? item;
        try
        {
            item = await downloader.GetItemAsync(release.ItemRef, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read job {JobId} from {Provider}.", release.JobId, release.Provider);
            var error = $"{release.Provider} is unreachable: {ex.Message}";
            unreachable.TryAdd(release.Provider, error);
            return new ReleaseView { Release = release, Status = release.Status, Error = error };
        }

        var observation = JobStatusEvaluator.Observe(
            release, item, _timeProvider.GetUtcNow(), _options.CurrentValue.StalledAfter);

        return new ReleaseView
        {
            Release = release,
            Status = observation.Status,
            Progress = item?.Size is > 0 ? Math.Clamp((double)item.Downloaded / item.Size.Value, 0, 1) : null,
            TotalBytes = item?.Size,
            DownloadedBytes = item?.Downloaded,
            SpeedBytesPerSecond = item?.DownloadSpeed,
            Error = observation.Error ?? release.Error,
        };
    }

    /// <summary>Checks one release of a request; nothing is added yet.</summary>
    private async Task<PreparedRelease> PrepareAsync(
        StartJobRequest request,
        int releaseCount,
        IReadOnlyList<PreparedRelease> earlier,
        RulesSnapshot rules,
        Dictionary<(string Provider, string? Category), string?> directories,
        CancellationToken cancellationToken)
    {
        // With several releases every message says which one it is about.
        var prefix = releaseCount > 1 ? $"'{request.Title}': " : string.Empty;

        if (!SubpathValidator.TryValidate(request.Subpath, out var subpath, out var subpathError))
        {
            throw new ConvyRequestException(prefix + subpathError);
        }

        var downloader = ResolveDownloader(request.Payload.Protocol);
        var options = new AddOptions(request.ClientCategory);

        // One job per download: a repeated request (e.g. after a client timeout) must not
        // create a second job that would never be placed.
        var itemRef = downloader.GetItemRef(request.Payload);
        if (await _store.FindActiveAsync(downloader.Provider, itemRef, cancellationToken).ConfigureAwait(false) is { } active)
        {
            throw new ConvyRequestException(
                $"{prefix}This download is already job {active.JobId} ({active.Status.ToName()}); check it with get_jobs or cancel it first.");
        }

        if (earlier.FirstOrDefault(e => e.Downloader.Provider == downloader.Provider && e.ItemRef == itemRef) is { } twin)
        {
            throw new ConvyRequestException($"{prefix}It is the same download as '{twin.Request.Title}'.");
        }

        var key = (downloader.Provider, request.ClientCategory);
        if (!directories.TryGetValue(key, out var downloadDirectory))
        {
            downloadDirectory = await TryGetDownloadDirectoryAsync(downloader, options, cancellationToken).ConfigureAwait(false);
            directories[key] = downloadDirectory;
        }

        // Check the sub-path against the expected base before anything is downloaded.
        var preliminary = BasicProperties(downloader.Provider, request);
        if (subpath is not null && downloadDirectory is not null)
        {
            var target = PlacementPlanner.ResolveTarget(rules.ResolveRule(preliminary), subpath, downloadDirectory);
            if (!SubpathValidator.IsInside(_fileSystem.GetRealPath(target.BaseDirectory!), _fileSystem.GetRealPath(target.Directory!)))
            {
                throw new ConvyRequestException($"{prefix}subpath resolves outside '{target.BaseDirectory}'.");
            }
        }

        return new PreparedRelease(request, downloader, options, itemRef, subpath, downloadDirectory, preliminary);
    }

    /// <summary>Adds one checked release to its downloader and forecasts its placement.</summary>
    private async Task<(PreparedRelease Release, JobRecord Record, string? ExpectedPath, string? Rule)> AddAsync(
        PreparedRelease release, RulesSnapshot rules, CancellationToken cancellationToken)
    {
        var request = release.Request;
        var downloader = release.Downloader;
        var itemRef = await downloader.AddAsync(request.Payload, request.Selection, release.Options, cancellationToken)
            .ConfigureAwait(false);

        // Forecast the placement with the item as the downloader reports it now, so the job
        // (and its first status event) already carries the expected rule and path.
        var item = await TryGetItemAsync(downloader, itemRef, cancellationToken).ConfigureAwait(false);
        var properties = item is null ? release.Preliminary : RuleInputs.For(item, request.ClientCategory);
        var savePath = item?.SavePath is { Length: > 0 } itemPath ? itemPath : release.DownloadDirectory;
        var rule = rules.ResolveRule(properties);
        var expected = PlacementPlanner.ResolveTarget(rule, release.Subpath, savePath ?? string.Empty);
        var expectedPath = expected.LeaveInPlace ? savePath : expected.Directory;
        var now = _timeProvider.GetUtcNow();

        var record = new JobRecord
        {
            Id = 0,
            Provider = downloader.Provider,
            ItemRef = itemRef,
            Category = request.Category,
            ClientCategory = request.ClientCategory,
            Subpath = release.Subpath,
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
        };

        _logger.LogInformation("Added '{Title}' to {Provider} ({ItemRef}).", request.Title, downloader.Provider, itemRef);
        return (release, record, expectedPath, rule?.Name);
    }

    /// <summary>
    /// Records the cancellation of one release first, then stops its download: once the
    /// release is final, the sync worker cannot turn the stopped download into "failed" in
    /// between. If the download cannot be stopped, the release is restored and the error rethrown.
    /// </summary>
    private async Task CancelReleaseAsync(JobRecord release, CancellationToken cancellationToken)
    {
        var downloader = _downloaders.FindByProvider(release.Provider)
                         ?? throw new ConvyRequestException($"Downloader '{release.Provider}' is not configured.");

        // The sync worker may update the release concurrently; retry on its fresh state.
        JobRecord? cancelled = null;
        for (var attempt = 0; attempt < 5 && cancelled is null; attempt++)
        {
            var now = _timeProvider.GetUtcNow();
            cancelled = await _transitions.SaveAsync(
                release, release with { Status = JobStatus.Cancelled, UpdatedAt = now, CompletedAt = now }, cancellationToken)
                .ConfigureAwait(false);

            if (cancelled is null)
            {
                release = await _store.GetAsync(release.Id, cancellationToken).ConfigureAwait(false) ?? release;
                if (release.Status.IsTerminal())
                {
                    return;
                }
            }
        }

        if (cancelled is null)
        {
            throw new InvalidOperationException($"Job {release.JobId} kept changing; cancellation was not recorded.");
        }

        try
        {
            await downloader.CancelAsync(release.ItemRef, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The download keeps running, so the release is not cancelled after all.
            await _transitions.SaveAsync(cancelled, release, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<JobState> GetRequiredAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!JobIds.TryParse(jobId, out var id))
        {
            throw new ConvyRequestException($"'{jobId}' is not a job id (expected e.g. j_42).");
        }

        var releases = await _store.GetGroupAsync(id, cancellationToken).ConfigureAwait(false);
        return releases.Count > 0
            ? JobState.From(releases)
            : throw new ConvyRequestException($"Job {jobId} does not exist.");
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

    /// <summary>A release that passed the checks and can be added.</summary>
    private sealed record PreparedRelease(
        StartJobRequest Request,
        IDownloader Downloader,
        AddOptions Options,
        string ItemRef,
        string? Subpath,
        string? DownloadDirectory,
        Dictionary<string, object?> Preliminary);
}
