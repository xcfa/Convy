namespace Convy.Services.Settings;

/// <summary>Persists user-overridable settings and triggers a configuration reload.</summary>
public interface IUserSettingsService
{
    Task UpdateAsync(UserSettingsUpdate update, CancellationToken cancellationToken);

    /// <summary>
    /// Saves settings by their configuration path (<c>Extensions:settings:prowlarr:values:url</c>), which then override
    /// configuration.yml and the environment; a <c>null</c> value removes the saved one. The settings under
    /// <paramref name="replace"/> not among <paramref name="entries"/> are removed first.
    /// </summary>
    Task WriteAsync(IReadOnlyDictionary<string, string?> entries, string? replace, CancellationToken cancellationToken);
}

public class UserSettingsUpdate
{
    public TimeSpan? SyncInterval { get; set; }
    public bool? AutoSyncEnabled { get; set; }
}
