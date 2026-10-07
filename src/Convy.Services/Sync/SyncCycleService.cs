using Convy.Data.Context;
using Convy.Data.Entities;
using Convy.Services.Downloads;
using Convy.Services.Linking;
using Convy.Services.Rules;
using Convy.Services.Tracking;
using Convy.Services.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Sync;

/// <summary>
/// Performs one sync cycle on demand: refreshes the routing rules, asks every downloader
/// for its items, lets the state tracker pick what changed, and hard-links the files of
/// matching items into their destinations. Downloaders are isolated from each other: one
/// failing does not stop the others.
/// </summary>
public sealed class SyncCycleService
{
    private readonly IDownloaderResolver _downloaders;
    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;
    private readonly IRulesProvider _rulesProvider;
    private readonly IDownloadStateTracker _tracker;
    private readonly FileLinkingService _linkingService;
    private readonly IWebhookNotifier _webhookNotifier;
    private readonly ILogger<SyncCycleService> _logger;

    private long _lastRulesVersion;

    public SyncCycleService(
        IDownloaderResolver downloaders,
        IDbContextFactory<ConvyDbContext> dbFactory,
        IRulesProvider rulesProvider,
        IDownloadStateTracker tracker,
        FileLinkingService linkingService,
        IWebhookNotifier webhookNotifier,
        ILogger<SyncCycleService> logger)
    {
        _downloaders = downloaders;
        _dbFactory = dbFactory;
        _rulesProvider = rulesProvider;
        _tracker = tracker;
        _linkingService = linkingService;
        _webhookNotifier = webhookNotifier;
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

    private async Task SyncDownloaderAsync(
        IDownloader downloader, RulesSnapshot rules, WebhookBatch webhookBatch, CancellationToken cancellationToken)
    {
        var provider = downloader.Provider;
        var items = await downloader.GetItemsAsync(cancellationToken).ConfigureAwait(false);

        var changes = await _tracker.ApplyAsync(provider, items, cancellationToken).ConfigureAwait(false);
        if (changes.Count == 0)
        {
            return;
        }

        _logger.LogInformation("{Count} {Provider} change(s) to process.", changes.Count, provider);

        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var processed = new List<string>();
        var skipped = new List<string>();

        foreach (var itemRef in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var outcome = await ProcessChangeAsync(downloader, context, itemRef, rules, webhookBatch, cancellationToken)
                    .ConfigureAwait(false);

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

    private async Task<ProcessOutcome> ProcessChangeAsync(
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

        var rule = rules.ResolveRule(item.Properties);
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

        var alreadyLinked = (await context.FileEntries
            .Where(x => x.Provider == provider && x.InfoHash == itemRef)
            .Select(x => x.FilePath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();

        // Only hand the linker the complete, selected files we haven't linked yet.
        var pending = item.Files
            .Where(f => f.IsComplete)
            .Select(f => f.Path)
            .Where(path => !alreadyLinked.Contains(path));

        var outcome = _linkingService.LinkFiles(item.SavePath, targetPath, pending);

        foreach (var path in outcome.NewlyLinked)
        {
            context.FileEntries.Add(new FileEntry
            {
                Provider = provider,
                InfoHash = itemRef,
                FilePath = path,
                TargetPath = Path.Combine(targetPath, path),
                TorrentName = item.Name,
                LinkedDate = DateTimeOffset.Now,
            });
        }

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

    /// <summary>
    /// The per-item properties sent in the <c>linked</c> webhook batch
    /// (hash, name, category, savePath, targetPath, size, state, tags).
    /// </summary>
    private static Dictionary<string, string> BuildWebhookProperties(DownloadItem item, string targetPath)
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

        return properties;
    }

    private enum ProcessOutcome
    {
        /// <summary>Matched a rule and every file is linked.</summary>
        Handled,

        /// <summary>Matched no rule; skip until the rules change.</summary>
        NoMatch,

        /// <summary>Matched but work remains (missing content / link failure); retry later.</summary>
        Retry,
    }
}
