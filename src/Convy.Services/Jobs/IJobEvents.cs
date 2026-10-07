using Microsoft.Extensions.Logging;

namespace Convy.Services.Jobs;

/// <summary>A job changed its status.</summary>
/// <param name="Job">The job after the change.</param>
/// <param name="PreviousStatus">The status before the change; <c>null</c> for a new job.</param>
/// <param name="PlacedFiles">Placed file paths relative to the target directory, when known.</param>
public sealed record JobStatusChange(JobRecord Job, JobStatus? PreviousStatus, IReadOnlyList<string>? PlacedFiles = null);

/// <summary>Receives job status changes (for notifications). Must not throw.</summary>
public interface IJobEvents
{
    Task PublishAsync(JobStatusChange change, CancellationToken cancellationToken);
}

/// <summary><see cref="IJobEvents"/> that only logs the change.</summary>
public sealed class LoggingJobEvents : IJobEvents
{
    private readonly ILogger<LoggingJobEvents> _logger;

    public LoggingJobEvents(ILogger<LoggingJobEvents> logger) => _logger = logger;

    public Task PublishAsync(JobStatusChange change, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Job {JobId} ({Title}): {Previous} -> {Status}",
            change.Job.JobId, change.Job.Title, change.PreviousStatus?.ToName() ?? "new", change.Job.Status.ToName());
        return Task.CompletedTask;
    }
}
