using Microsoft.Extensions.Configuration;

namespace Convy.Services.Jobs;

/// <summary>A job as the services see it (immutable snapshot of a stored job).</summary>
public sealed record JobRecord
{
    public required int Id { get; init; }

    /// <summary>Public identifier, e.g. <c>j_42</c>.</summary>
    public string JobId => JobIds.Format(Id);

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
