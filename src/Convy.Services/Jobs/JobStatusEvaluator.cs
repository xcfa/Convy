using Convy.Services.Downloads;

namespace Convy.Services.Jobs;

/// <summary>What the downloader currently says about a job.</summary>
/// <param name="Status">The status derived from the downloader item.</param>
/// <param name="DownloadedBytes">Downloaded bytes to remember for stall detection.</param>
/// <param name="LastProgressAt">When progress was last seen.</param>
/// <param name="Error">Error to report, if any.</param>
public sealed record JobObservation(JobStatus Status, long DownloadedBytes, DateTimeOffset LastProgressAt, string? Error);

/// <summary>
/// Derives a job's status from its downloader item. The downloader decides queued /
/// downloading / failed; Convy adds <see cref="JobStatus.Stalled"/> (no progress for too long,
/// or a problem the downloader may recover from) and keeps <see cref="JobStatus.Placing"/>
/// until the sync worker has placed the files. Time spent waiting in a queue is not counted
/// as a stall.
/// </summary>
public static class JobStatusEvaluator
{
    /// <param name="job">The stored job.</param>
    /// <param name="item">The downloader item, or <c>null</c> when the downloader no longer has it.</param>
    /// <param name="now">Current time.</param>
    /// <param name="stalledAfter">How long without progress makes a download stalled.</param>
    public static JobObservation Observe(JobRecord job, DownloadItem? item, DateTimeOffset now, TimeSpan stalledAfter)
    {
        if (job.Status.IsTerminal())
        {
            return new JobObservation(job.Status, job.LastDownloadedBytes, job.LastProgressAt, job.Error);
        }

        if (item is null)
        {
            return new JobObservation(
                JobStatus.Failed, job.LastDownloadedBytes, job.LastProgressAt,
                $"The download was removed from {job.Provider}.");
        }

        var waiting = item.State is DownloadState.Queued or DownloadState.Paused;
        var progressed = item.Downloaded > job.LastDownloadedBytes;

        // Leaving a queue starts the stall clock afresh: time spent waiting is not a stall.
        // While waiting nothing changes, so a queued job is not rewritten every cycle.
        var leftQueue = job.Status == JobStatus.Queued && !waiting;
        var lastProgressAt = progressed || leftQueue ? now : job.LastProgressAt;

        var status = item.State switch
        {
            DownloadState.Completed => JobStatus.Placing,
            DownloadState.Failed => JobStatus.Failed,
            DownloadState.Errored => JobStatus.Stalled,
            _ when waiting => JobStatus.Queued,
            // A placing job whose files are being moved/checked stays placing.
            _ when job.Status == JobStatus.Placing => JobStatus.Placing,
            _ when now - lastProgressAt >= stalledAfter => JobStatus.Stalled,
            _ => JobStatus.Downloading,
        };

        var error = status switch
        {
            JobStatus.Failed => item.Error ?? $"{job.Provider} reports the download as failed.",
            JobStatus.Stalled => item.Error,
            _ => null,
        };

        return new JobObservation(status, item.Downloaded, lastProgressAt, error);
    }
}
