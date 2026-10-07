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
