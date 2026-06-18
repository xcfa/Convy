namespace Convy.Services.Settings;

/// <summary>
/// User-overridable settings persisted in the database.
/// <c>null</c> means "use the default from configuration".
/// Bound via a custom DB-backed configuration provider and available
/// as <c>IOptionsMonitor&lt;UserSettings&gt;</c>.
/// </summary>
public class UserSettings
{
    public TimeSpan? SyncInterval { get; set; }
    public bool? AutoSyncEnabled { get; set; }
}
