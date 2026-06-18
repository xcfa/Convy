namespace Convy.Services;

/// <summary>
/// Allows triggering a one-off sync cycle and checking whether
/// the background loop is currently syncing.
/// </summary>
public interface ISyncTrigger
{
    /// <summary>Wakes the background loop for a single sync cycle.</summary>
    void TriggerSync();

    /// <summary><c>true</c> while a sync cycle is executing.</summary>
    bool IsSyncing { get; }
}
