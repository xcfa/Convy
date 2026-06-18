using System.ComponentModel.DataAnnotations;

namespace Convy.Services.Sync;

/// <summary>Reads sync state and applies sync-related setting changes.</summary>
public interface ISyncControlService
{
    /// <summary>Returns the current sync state (effective interval, auto-sync, running flag).</summary>
    SyncStatusDto GetStatus();

    /// <summary>
    /// Persists the provided sync settings (only non-null fields are changed)
    /// and returns the resulting state.
    /// </summary>
    Task<SyncStatusDto> UpdateSettingsAsync(SyncSettingsUpdateDto dto, CancellationToken cancellationToken);

    /// <summary>
    /// Triggers a one-off sync cycle. Returns <c>false</c> if a cycle is
    /// already running, in which case nothing is triggered.
    /// </summary>
    bool TryTriggerSync();
}

/// <summary>Current sync state.</summary>
public class SyncStatusDto
{
    public bool AutoSyncEnabled { get; set; }
    public double IntervalSeconds { get; set; }
    public bool IsSyncing { get; set; }
}

/// <summary>Partial update for sync settings. Only non-null fields are applied.</summary>
public class SyncSettingsUpdateDto : IValidatableObject
{
    public bool? AutoSyncEnabled { get; set; }
    public double? IntervalSeconds { get; set; }

    /// <summary>Rejects intervals below the minimum allowed sync interval.</summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IntervalSeconds.HasValue
            && TimeSpan.FromSeconds(IntervalSeconds.Value) < QBitTorrentConnectionSettings.MinSyncInterval)
        {
            yield return new ValidationResult(
                $"Interval must be >= {QBitTorrentConnectionSettings.MinSyncInterval.TotalSeconds}s.",
                new[] { nameof(IntervalSeconds) });
        }
    }
}
