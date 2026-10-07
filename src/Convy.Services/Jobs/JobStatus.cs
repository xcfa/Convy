namespace Convy.Services.Jobs;

/// <summary>Unified status of a job, the same for every downloader.</summary>
public enum JobStatus
{
    /// <summary>Added but not downloading yet (client queue, peer queue, fetching metadata).</summary>
    Queued,

    /// <summary>Downloading.</summary>
    Downloading,

    /// <summary>No progress for longer than the configured threshold (no seeds, peer offline).</summary>
    Stalled,

    /// <summary>The client finished; placement waits for a sync cycle or a retry after an error.</summary>
    Placing,

    /// <summary>Files are at the target path (or left in place when no rule and no sub-path apply).</summary>
    Completed,

    /// <summary>Downloader error, peer refusal, or placement attempts exhausted.</summary>
    Failed,

    /// <summary>Cancelled through <c>cancel_job</c>.</summary>
    Cancelled,
}

/// <summary>Conversions between <see cref="JobStatus"/> and its wire/storage name.</summary>
public static class JobStatusNames
{
    /// <summary>The lower-case name used in storage, MCP responses and webhooks.</summary>
    public static string ToName(this JobStatus status) => status switch
    {
        JobStatus.Queued => "queued",
        JobStatus.Downloading => "downloading",
        JobStatus.Stalled => "stalled",
        JobStatus.Placing => "placing",
        JobStatus.Completed => "completed",
        JobStatus.Failed => "failed",
        JobStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>Parses a status name (case-insensitive).</summary>
    public static bool TryParse(string? name, out JobStatus status)
    {
        foreach (var candidate in Enum.GetValues<JobStatus>())
        {
            if (string.Equals(candidate.ToName(), name, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }

    /// <summary>Parses a stored status name; throws on unknown values.</summary>
    public static JobStatus Parse(string name) =>
        TryParse(name, out var status) ? status : throw new FormatException($"Unknown job status '{name}'.");

    /// <summary>Whether the status is final (the job no longer changes on its own).</summary>
    public static bool IsTerminal(this JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
}
