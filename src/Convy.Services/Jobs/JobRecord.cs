using Microsoft.Extensions.Configuration;

namespace Convy.Services.Jobs;

/// <summary>
/// One release of a job: a single downloader item with its placement wishes and status
/// (immutable snapshot of a stored entry). A job (<see cref="JobState"/>) is every release
/// with the same <see cref="GroupId"/>; the sync worker handles releases one by one.
/// </summary>
public sealed record JobRecord
{
    /// <summary>Id of this release.</summary>
    public required int Id { get; init; }

    /// <summary>Id of the job this release belongs to; assigned by the store (0 before).</summary>
    public int GroupId { get; init; }

    /// <summary>Public identifier of the job, e.g. <c>j_42</c>.</summary>
    public string JobId => JobIds.Format(GroupId == 0 ? Id : GroupId);

    public required string Provider { get; init; }
    public required string ItemRef { get; init; }

    /// <summary>Category id from the configuration.</summary>
    public required string Category { get; init; }

    /// <summary>Client category; overrides <c>Category</c> for the rules.</summary>
    public string? ClientCategory { get; init; }

    public string? Subpath { get; init; }

    /// <summary>Selected paths relative to the result root, or <c>null</c> for every file.</summary>
    public IReadOnlyList<string>? SelectedFiles { get; init; }

    public required string Title { get; init; }
    public long? SizeBytes { get; init; }
    public int? FileCount { get; init; }
    public string? ResultId { get; init; }
    public string? SourceId { get; init; }

    public required JobStatus Status { get; init; }
    public string? Rule { get; init; }
    public string? TargetPath { get; init; }
    public string? Error { get; init; }

    /// <summary>Placed file paths relative to <see cref="TargetPath"/>, once completed.</summary>
    public IReadOnlyList<string>? PlacedFiles { get; init; }

    public int PlacementAttempts { get; init; }
    public long LastDownloadedBytes { get; init; }
    public DateTimeOffset LastProgressAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Formats and parses public job ids (<c>j_42</c>).</summary>
public static class JobIds
{
    private const string Prefix = "j_";

    public static string Format(int id) => Prefix + id;

    public static bool TryParse(string? jobId, out int id)
    {
        id = 0;
        return jobId is not null
               && jobId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(jobId.AsSpan(Prefix.Length), out id)
               && id > 0;
    }
}

/// <summary>Configuration of jobs (<c>jobs</c> section of configuration.yml).</summary>
public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>A job without progress for this long is reported as stalled.</summary>
    [ConfigurationKeyName("stalled_after_min")]
    public double StalledAfterMinutes { get; set; } = 30;

    /// <summary>Placement attempts before a job fails.</summary>
    [ConfigurationKeyName("max_placement_attempts")]
    public int MaxPlacementAttempts { get; set; } = 10;

    /// <summary>Largest allowed job in GiB; 0 disables the limit.</summary>
    [ConfigurationKeyName("max_size_gb")]
    public double MaxSizeGb { get; set; }

    /// <summary>Free space in GiB that must remain in the download directory after the job; 0 disables the check.</summary>
    [ConfigurationKeyName("min_free_space_gb")]
    public double MinFreeSpaceGb { get; set; }

    public TimeSpan StalledAfter => TimeSpan.FromMinutes(StalledAfterMinutes);
}
