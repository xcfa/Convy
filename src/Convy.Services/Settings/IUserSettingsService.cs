namespace Convy.Services.Settings;

/// <summary>Persists user-overridable settings and triggers a configuration reload.</summary>
public interface IUserSettingsService
{
    Task UpdateAsync(UserSettingsUpdate update, CancellationToken cancellationToken);
}

public class UserSettingsUpdate
{
    public TimeSpan? SyncInterval { get; set; }
    public bool? AutoSyncEnabled { get; set; }
}
