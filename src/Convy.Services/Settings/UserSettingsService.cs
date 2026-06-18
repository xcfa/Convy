using Convy.Data.Context;
using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Convy.Services.Settings;

/// <summary>
/// Writes user settings to the database and triggers a configuration
/// provider reload so <c>IOptionsMonitor&lt;UserSettings&gt;</c> picks
/// up the changes immediately.
/// </summary>
public sealed class UserSettingsService : IUserSettingsService
{
    private readonly IDbContextFactory<SettingsDbContext> _dbFactory;
    private readonly Func<CancellationToken, Task> _onSettingsChanged;

    public UserSettingsService(IDbContextFactory<SettingsDbContext> dbFactory, Func<CancellationToken, Task> onSettingsChanged)
    {
        _dbFactory = dbFactory;
        _onSettingsChanged = onSettingsChanged;
    }

    public async Task UpdateAsync(UserSettingsUpdate update, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        if (update.SyncInterval.HasValue)
        {
            await UpsertAsync(db, nameof(UserSettings.SyncInterval),
                update.SyncInterval.Value.ToString(), cancellationToken).ConfigureAwait(false);
        }

        if (update.AutoSyncEnabled.HasValue)
        {
            await UpsertAsync(db, nameof(UserSettings.AutoSyncEnabled),
                update.AutoSyncEnabled.Value.ToString(), cancellationToken).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _onSettingsChanged(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(
        SettingsDbContext db, string key, string value, CancellationToken cancellationToken)
    {
        var entry = await db.UserSettings
            .FirstOrDefaultAsync(e => e.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (entry is not null)
        {
            entry.Value = value;
        }
        else
        {
            db.UserSettings.Add(new UserSettingEntry { Key = key, Value = value });
        }
    }
}
