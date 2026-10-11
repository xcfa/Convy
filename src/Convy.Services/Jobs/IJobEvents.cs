namespace Convy.Services.Jobs;

/// <summary>A job changed its (combined) status.</summary>
/// <param name="Job">The job after the change.</param>
/// <param name="PreviousStatus">The status before the change; <c>null</c> for a new job.</param>
public sealed record JobStatusChange(JobState Job, JobStatus? PreviousStatus);

/// <summary>Receives job status changes (for notifications). Must not throw.</summary>
public interface IJobEvents
{
    Task PublishAsync(JobStatusChange change, CancellationToken cancellationToken);
}
