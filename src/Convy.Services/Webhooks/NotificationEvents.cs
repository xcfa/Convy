using System.Collections.Concurrent;
using Convy.Services.Jobs;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Webhooks;

/// <summary>Turns job status changes into <c>job_status</c> webhook events.</summary>
public sealed class WebhookJobEvents : IJobEvents
{
    /// <summary>Most files listed in one event; <c>files_total</c> carries the full count.</summary>
    public const int MaxFiles = 200;

    private readonly IWebhookEventQueue _queue;
    private readonly ILogger<WebhookJobEvents> _logger;

    public WebhookJobEvents(IWebhookEventQueue queue, ILogger<WebhookJobEvents> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public Task PublishAsync(JobStatusChange change, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Job {JobId} ({Title}): {Previous} -> {Status}",
            change.Job.JobId, change.Job.Title, change.PreviousStatus?.ToName() ?? "new", change.Job.Status.ToName());

        try
        {
            _queue.Enqueue(ToEvent(change));
        }
        catch (Exception ex)
        {
            // The change is already saved; a notification problem must not fail it.
            _logger.LogError(ex, "Could not queue the job_status event of {JobId}.", change.Job.JobId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The <c>job_status</c> body. A job with several releases also lists them under
    /// <c>releases</c>; its <c>path</c> is the directory containing all of them and
    /// <c>files</c> are relative to it.
    /// </summary>
    public static WebhookEvent ToEvent(JobStatusChange change)
    {
        var job = change.Job;
        var files = job.PlacedFiles;

        var payload = new Dictionary<string, object?>
        {
            ["event"] = WebhookEvents.JobStatus,
            ["job_id"] = job.JobId,
            ["status"] = job.Status.ToName(),
            ["previous_status"] = change.PreviousStatus?.ToName(),
            ["provider"] = job.Provider,
            ["category"] = job.Category,
            ["rule"] = job.Rule,
            ["title"] = job.Title,
            ["path"] = job.TargetPath,
            ["files"] = files.Take(MaxFiles).ToList(),
            ["files_total"] = files.Count > 0 ? files.Count : job.FileCount ?? 0,
            ["size_bytes"] = job.SizeBytes,
            ["error"] = job.Error,
        };

        if (job.HasManyReleases)
        {
            payload["releases"] = job.Releases
                .Select(r => new Dictionary<string, object?>
                {
                    ["title"] = r.Title,
                    ["status"] = r.Status.ToName(),
                    ["provider"] = r.Provider,
                    ["rule"] = r.Rule,
                    ["path"] = r.TargetPath,
                    ["size_bytes"] = r.SizeBytes,
                    ["error"] = r.Error,
                })
                .ToList();
        }

        var rules = job.Releases.Select(r => r.Rule).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return new WebhookEvent(WebhookEvents.JobStatus, rules, payload);
    }
}

/// <summary>Receives the observed health of sources.</summary>
public interface ISourceHealth
{
    /// <param name="sourceId">Source id.</param>
    /// <param name="status">Search status (<c>ok</c>, <c>empty</c>, <c>timeout</c>, <c>auth_failed</c>, <c>error</c>).</param>
    /// <param name="message">Reason of a failure.</param>
    void Report(string sourceId, string status, string? message);
}

/// <summary>
/// Remembers the last status of every source and raises <c>source_error</c> when a source
/// turns <c>auth_failed</c> or <c>error</c> (not again while it stays so). A timeout says
/// nothing about health and is ignored. Kept in memory: after a restart the first failure
/// is reported again.
/// </summary>
public sealed class SourceHealthMonitor : ISourceHealth
{
    private readonly IWebhookEventQueue _queue;
    private readonly ILogger<SourceHealthMonitor> _logger;
    private readonly ConcurrentDictionary<string, string> _last = new(StringComparer.OrdinalIgnoreCase);

    public SourceHealthMonitor(IWebhookEventQueue queue, ILogger<SourceHealthMonitor> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public void Report(string sourceId, string status, string? message)
    {
        if (status == "timeout")
        {
            return;
        }

        var previous = _last.GetValueOrDefault(sourceId);
        _last[sourceId] = status;

        if (status is "auth_failed" or "error" && previous != status)
        {
            _logger.LogWarning("Source {Source} is now {Status}: {Message}", sourceId, status, message);
            _queue.Enqueue(new WebhookEvent(WebhookEvents.SourceError, null, new Dictionary<string, object?>
            {
                ["event"] = WebhookEvents.SourceError,
                ["source"] = sourceId,
                ["status"] = status,
                ["message"] = message,
            }));
        }
    }
}
