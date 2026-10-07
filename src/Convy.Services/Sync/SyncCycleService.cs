using Convy.Data.Context;
using Convy.Data.Entities;
using Convy.Infrastructure.Helpers;
using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Linking;
using Convy.Services.Placement;
using Convy.Services.Rules;
using Convy.Services.Storage;
using Convy.Services.Tracking;
using Convy.Services.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Sync;

/// <summary>
/// Performs one sync cycle on demand: refreshes the routing rules, asks every downloader
/// for its items, updates the status of agent jobs, and hard-links the files of finished
/// items into their destinations. Downloaders are isolated from each other: one failing does
/// not stop the others.
/// </summary>
/// <remarks>
/// Items that belong to a job are placed according to the job (sub-path, file selection,
/// job category); every other item is placed by the rules alone, exactly as before jobs existed.
/// </remarks>
public sealed class SyncCycleService
{
    private readonly IDownloaderResolver _downloaders;
    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;
    private readonly IRulesProvider _rulesProvider;
    private readonly IDownloadStateTracker _tracker;
    private readonly FileLinkingService _linkingService;
    private readonly IWebhookNotifier _webhookNotifier;
    private readonly IJobStore _jobStore;
    private readonly JobTransitions _transitions;
    private readonly IOptionsMonitor<JobOptions> _jobOptions;
    private readonly IFileSystemInspector _fileSystem;
    private readonly StorageLayoutValidator _storageValidator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncCycleService> _logger;

    private long _lastRulesVersion;
    private bool _storageCheckPending = true;

    public SyncCycleService(
        IDownloaderResolver downloaders,
        IDbContextFactory<ConvyDbContext> dbFactory,
        IRulesProvider rulesProvider,
        IDownloadStateTracker tracker,
        FileLinkingService linkingService,
        IWebhookNotifier webhookNotifier,
        IJobStore jobStore,
        JobTransitions transitions,
        IOptionsMonitor<JobOptions> jobOptions,
        IFileSystemInspector fileSystem,
        StorageLayoutValidator storageValidator,
        TimeProvider timeProvider,
        ILogger<SyncCycleService> logger)
    {
        _downloaders = downloaders;
        _dbFactory = dbFactory;
        _rulesProvider = rulesProvider;
        _tracker = tracker;
        _linkingService = linkingService;
        _webhookNotifier = webhookNotifier;
        _jobStore = jobStore;
        _transitions = transitions;
        _jobOptions = jobOptions;
        _fileSystem = fileSystem;
        _storageValidator = storageValidator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Capture an immutable rules snapshot once per cycle; a concurrent reload only
        // affects the next cycle, so there is no race with this run.
        var rules = _rulesProvider.GetCurrent();

        if (rules.Version != _lastRulesVersion)
        {
            // Rules changed: forget skipped items so they are re-emitted below and
            // re-evaluated against the new rules.
            await _tracker.ClearSkippedAsync(cancellationToken).ConfigureAwait(false);
            _lastRulesVersion = rules.Version;
            _storageCheckPending = true;
        }

        if (_storageCheckPending)
        {
            await CheckStorageAsync(rules, cancellationToken).ConfigureAwait(false);
        }

        var webhookBatch = new WebhookBatch();

        foreach (var downloader in _downloaders.All)
        {
            try
            {
                await SyncDownloaderAsync(downloader, rules, webhookBatch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Provider} sync failed.", downloader.Provider);
            }
        }

        if (webhookBatch.HasEntries)
        {
            try
            {
                await _webhookNotifier.NotifyAsync(webhookBatch, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Webhook notification failed.");
            }
        }
    }

    private async Task CheckStorageAsync(RulesSnapshot rules, CancellationToken cancellationToken)
    {
        try
        {
            // Repeat on the next cycle while a downloader could not be asked.
            _storageCheckPending = !await _storageValidator.ValidateAsync(rules, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Storage layout check failed.");
        }
    }

    private async Task SyncDownloaderAsync(
        IDownloader downloader, RulesSnapshot rules, WebhookBatch webhookBatch, CancellationToken cancellationToken)
    {
        var provider = downloader.Provider;

        // Jobs are read before the item list: a job created after this read had its item
        // added after it too, so it can never be mistaken for a download that vanished.
        var jobs = await _jobStore.GetPlacementJobsAsync(provider, cancellationToken).ConfigureAwait(false);
        var items = await downloader.GetItemsAsync(cancellationToken).ConfigureAwait(false);

        var changes = await _tracker.ApplyAsync(provider, items, cancellationToken).ConfigureAwait(false);
        var jobsByItem = await RefreshJobsAsync(jobs, items, cancellationToken).ConfigureAwait(false);

        // Finished job items are placed even if the tracker has seen them before (e.g. an
        // agent download that reused a torrent already in the client).
        var work = changes.ToList();
        foreach (var job in jobsByItem.Values)
        {
            if (job.Status == JobStatus.Placing && !work.Contains(job.ItemRef))
            {
                work.Add(job.ItemRef);
            }
        }

        if (work.Count == 0)
        {
            return;
        }

        _logger.LogInformation("{Count} {Provider} change(s) to process.", work.Count, provider);

        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var processed = new List<string>();
        var skipped = new List<string>();

        foreach (var itemRef in work)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var outcome = jobsByItem.TryGetValue(itemRef, out var job)
                    ? await ProcessJobAsync(downloader, context, job, rules, webhookBatch, cancellationToken).ConfigureAwait(false)
                    : await ProcessManualAsync(downloader, context, itemRef, rules, webhookBatch, cancellationToken).ConfigureAwait(false);

                switch (outcome)
                {
                    case ProcessOutcome.Handled:
                        processed.Add(itemRef);
                        break;

                    case ProcessOutcome.NoMatch:
                        skipped.Add(itemRef);
                        break;

                    case ProcessOutcome.Retry:
                        // Neither confirmed nor skipped -> re-emitted on a later cycle.
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolate per item: one failure must neither abort the batch nor
                // advance this item's baseline.
                _logger.LogError(ex, "Failed to process {Provider} item {ItemRef}.", provider, itemRef);
                webhookBatch.Errors.Add(new WebhookError(itemRef, ex.Message));
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Advance the tracker baseline only after the links are durably recorded.
        await _tracker.ConfirmProcessedAsync(provider, processed, cancellationToken).ConfigureAwait(false);
        await _tracker.MarkSkippedAsync(provider, skipped, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the status of every active job from the downloader's item list and returns
    /// the job that drives placement for each item (the newest one).
    /// </summary>
    private async Task<Dictionary<string, JobRecord>> RefreshJobsAsync(
        IReadOnlyList<JobRecord> jobs, IReadOnlyList<DownloadItem> items, CancellationToken cancellationToken)
    {
        var itemsByRef = new Dictionary<string, DownloadItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            itemsByRef.TryAdd(item.ItemRef, item);
        }

        var now = _timeProvider.GetUtcNow();
        var stalledAfter = _jobOptions.CurrentValue.StalledAfter;
        var latest = new Dictionary<string, JobRecord>(StringComparer.Ordinal);

        foreach (var job in jobs)
        {
            var current = job;

            if (!job.Status.IsTerminal())
            {
                var observation = JobStatusEvaluator.Observe(job, itemsByRef.GetValueOrDefault(job.ItemRef), now, stalledAfter);
                var updated = job with
                {
                    Status = observation.Status,
                    LastDownloadedBytes = observation.DownloadedBytes,
                    LastProgressAt = observation.LastProgressAt,
                    Error = observation.Status == JobStatus.Failed ? observation.Error : job.Error,
                };

                if (updated != job)
                {
                    updated = updated with
                    {
                        UpdatedAt = now,
                        CompletedAt = updated.Status.IsTerminal() ? now : null,
                    };

                    current = await _transitions.ApplyAsync(job, updated, cancellationToken).ConfigureAwait(false)
                              ?? await _jobStore.GetAsync(job.Id, cancellationToken).ConfigureAwait(false)
                              ?? job;
                }
            }

            if (current.Status != JobStatus.Cancelled)
            {
                latest[current.ItemRef] = current;
            }
        }

        return latest;
    }

    private async Task<ProcessOutcome> ProcessJobAsync(
        IDownloader downloader,
        ConvyDbContext context,
        JobRecord job,
        RulesSnapshot rules,
        WebhookBatch webhookBatch,
        CancellationToken cancellationToken)
    {
        if (job.Status == JobStatus.Failed)
        {
            // A failed job is not placed automatically; it is re-evaluated after a rules
            // change or a restart and skipped again.
            return ProcessOutcome.NoMatch;
        }

        var item = await downloader.GetItemAsync(job.ItemRef, cancellationToken).ConfigureAwait(false);
        if (item is null || !item.IsDownloaded)
        {
            // The status refresh reports removal or a regression; nothing to place yet.
            return ProcessOutcome.Retry;
        }

        var rule = rules.ResolveRule(RuleInputs.For(item, job.ClientCategory));
        var target = PlacementPlanner.ResolveTarget(rule, job.Subpath, item.SavePath);
        var now = _timeProvider.GetUtcNow();

        if (target.LeaveInPlace)
        {
            _logger.LogInformation(
                "Job {JobId}: no rule and no sub-path; files stay in '{SavePath}'.", job.JobId, item.SavePath);
            await CompleteJobAsync(job, null, item.SavePath, [], now, cancellationToken).ConfigureAwait(false);
            return ProcessOutcome.Handled;
        }

        if (target.ReplaceRoot
            && !SubpathValidator.IsInside(_fileSystem.GetRealPath(target.BaseDirectory!), _fileSystem.GetRealPath(target.Directory!)))
        {
            await FailJobAsync(job, $"Sub-path resolves outside '{target.BaseDirectory}'.", now, cancellationToken)
                .ConfigureAwait(false);
            return ProcessOutcome.NoMatch;
        }

        var selection = job.SelectedFiles is null ? null : new HashSet<string>(job.SelectedFiles, StringComparer.Ordinal);
        var root = target.ReplaceRoot ? PlacementPlanner.FindRoot(item.Files) : null;
        var wanted = item.Files
            .Where(f => f.IsComplete && PlacementPlanner.IsInSelection(f.Path, selection))
            .Select(f => f.Path)
            .ToList();

        var alreadyLinked = await LoadLinkedAsync(context, item.Provider, item.ItemRef, cancellationToken).ConfigureAwait(false);
        var links = PlacementPlanner.PlanLinks(wanted.Where(p => !alreadyLinked.Contains(p)), target.Directory!, root);

        _logger.LogInformation("Job {JobId}: {Provider} item {ItemRef} -> {Target}", job.JobId, item.Provider, item.ItemRef, target.Directory);

        var outcome = _linkingService.LinkPlanned(item.SavePath, links);
        RecordLinks(context, item, links, outcome);

        if (!outcome.AllLinked)
        {
            var attempts = job.PlacementAttempts + 1;
            var reason = outcome.Errors.Count > 0
                ? string.Join("; ", outcome.Errors.Distinct())
                : $"{outcome.MissingSources} file(s) not found under '{item.SavePath}'";

            if (attempts >= _jobOptions.CurrentValue.MaxPlacementAttempts)
            {
                await FailJobAsync(job, $"Placement failed after {attempts} attempt(s): {reason}", now, cancellationToken)
                    .ConfigureAwait(false);
                return ProcessOutcome.NoMatch;
            }

            await _transitions.ApplyAsync(
                job, job with { PlacementAttempts = attempts, Error = reason, UpdatedAt = now }, cancellationToken)
                .ConfigureAwait(false);
            return ProcessOutcome.Retry;
        }

        var placedFiles = wanted.Select(path => PlacementPlanner.MapPath(path, root)).ToList();
        await CompleteJobAsync(job, rule?.Name, target.Directory!, placedFiles, now, cancellationToken).ConfigureAwait(false);

        if (outcome.NewlyLinked.Count > 0)
        {
            webhookBatch.AddLinked(rule?.Name, BuildWebhookProperties(item, target.Directory!, job));
        }

        return ProcessOutcome.Handled;
    }

    private async Task<ProcessOutcome> ProcessManualAsync(
        IDownloader downloader,
        ConvyDbContext context,
        string itemRef,
        RulesSnapshot rules,
        WebhookBatch webhookBatch,
        CancellationToken cancellationToken)
    {
        var provider = downloader.Provider;

        var item = await downloader.GetItemAsync(itemRef, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            _logger.LogWarning("{Provider} item {ItemRef} info unavailable; will retry.", provider, itemRef);
            return ProcessOutcome.Retry;
        }

        var rule = rules.ResolveRule(RuleInputs.For(item));
        if (rule is null)
        {
            _logger.LogDebug("No rule matched {Provider} item {ItemRef}; marking skipped.", provider, itemRef);
            return ProcessOutcome.NoMatch;
        }

        var targetPath = rule.OutputPath;

        if (string.IsNullOrEmpty(item.SavePath))
        {
            _logger.LogWarning("{Provider} item {ItemRef} has no save path yet; will retry.", provider, itemRef);
            return ProcessOutcome.Retry;
        }

        _logger.LogInformation("{Provider} item {ItemRef} -> {Target}", provider, itemRef, targetPath);

        var alreadyLinked = await LoadLinkedAsync(context, provider, itemRef, cancellationToken).ConfigureAwait(false);

        // Only hand the linker the complete, selected files we haven't linked yet.
        var pending = item.Files
            .Where(f => f.IsComplete)
            .Select(f => f.Path)
            .Where(path => !alreadyLinked.Contains(path));

        var links = PlacementPlanner.PlanLinks(pending, targetPath, stripRoot: null);
        var outcome = _linkingService.LinkPlanned(item.SavePath, links);
        RecordLinks(context, item, links, outcome);

        if (outcome.MissingSources > 0)
        {
            _logger.LogWarning(
                "{Provider} item {ItemRef}: {Missing} file(s) not found under '{SavePath}' inside the container. " +
                "Is the download path mounted here at the same absolute path? Will retry.",
                provider, itemRef, outcome.MissingSources, item.SavePath);
        }

        if (!outcome.AllLinked)
        {
            return ProcessOutcome.Retry;
        }

        webhookBatch.AddLinked(rule.Name, BuildWebhookProperties(item, targetPath));
        return ProcessOutcome.Handled;
    }

    private Task CompleteJobAsync(
        JobRecord job, string? ruleName, string targetPath, IReadOnlyList<string> placedFiles,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var completed = job with
        {
            Status = JobStatus.Completed,
            Rule = ruleName,
            TargetPath = targetPath,
            Error = null,
            UpdatedAt = job.Status == JobStatus.Completed && job.TargetPath == targetPath ? job.UpdatedAt : now,
            CompletedAt = job.CompletedAt ?? now,
        };

        return _transitions.ApplyAsync(job, completed, cancellationToken, placedFiles);
    }

    private Task FailJobAsync(JobRecord job, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _logger.LogError("Job {JobId} failed: {Error}", job.JobId, error);
        return _transitions.ApplyAsync(
            job, job with { Status = JobStatus.Failed, Error = error, UpdatedAt = now, CompletedAt = now }, cancellationToken);
    }

    private static async Task<HashSet<string>> LoadLinkedAsync(
        ConvyDbContext context, string provider, string itemRef, CancellationToken cancellationToken) =>
        (await context.FileEntries
            .Where(x => x.Provider == provider && x.InfoHash == itemRef)
            .Select(x => x.FilePath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();

    private static void RecordLinks(ConvyDbContext context, DownloadItem item, IReadOnlyList<PlannedLink> links, LinkOutcome outcome)
    {
        var destinations = links.ToDictionary(l => l.Source, l => l.Destination, StringComparer.Ordinal);

        foreach (var path in outcome.NewlyLinked)
        {
            context.FileEntries.Add(new FileEntry
            {
                Provider = item.Provider,
                InfoHash = item.ItemRef,
                FilePath = path,
                TargetPath = destinations[path],
                TorrentName = item.Name,
                LinkedDate = DateTimeOffset.Now,
            });
        }
    }

    /// <summary>
    /// The per-item properties sent in the <c>linked</c> webhook batch (hash, name, category,
    /// savePath, targetPath, size, state, tags; plus job_id and provider for job items).
    /// </summary>
    private static Dictionary<string, string> BuildWebhookProperties(DownloadItem item, string targetPath, JobRecord? job = null)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hash"] = item.ItemRef,
            ["name"] = item.Name,
            ["category"] = item.Properties.GetValueOrDefault("Category") as string ?? "",
            ["savePath"] = item.SavePath,
            ["targetPath"] = targetPath,
            ["size"] = item.Size?.ToString() ?? "0",
            ["state"] = item.Properties.GetValueOrDefault("State") as string ?? "",
        };

        if (item.Properties.GetValueOrDefault("Tags") is IEnumerable<string> tags)
        {
            properties["tags"] = string.Join(",", tags);
        }

        if (job is not null)
        {
            properties["job_id"] = job.JobId;
            properties["provider"] = item.Provider;
            properties["category"] = job.ClientCategory ?? properties["category"];
        }

        return properties;
    }

    private enum ProcessOutcome
    {
        /// <summary>Placed (or deliberately left in place); nothing left to do.</summary>
        Handled,

        /// <summary>Matched no rule, or the job failed; skip until the rules change.</summary>
        NoMatch,

        /// <summary>Work remains (missing content / link failure); retry later.</summary>
        Retry,
    }
}
