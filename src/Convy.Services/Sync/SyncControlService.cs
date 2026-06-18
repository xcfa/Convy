using Convy.Services.Settings;
using Microsoft.Extensions.Options;

namespace Convy.Services.Sync;

/// <inheritdoc />
public sealed class SyncControlService : ISyncControlService
{
    private readonly IUserSettingsService _settingsService;
    private readonly ISyncTrigger _syncTrigger;
    private readonly IOptionsMonitor<UserSettings> _userSettings;
    private readonly IOptions<QBitTorrentConnectionSettings> _connectionSettings;

    public SyncControlService(
        IUserSettingsService settingsService,
        ISyncTrigger syncTrigger,
        IOptionsMonitor<UserSettings> userSettings,
        IOptions<QBitTorrentConnectionSettings> connectionSettings)
    {
        _settingsService = settingsService;
        _syncTrigger = syncTrigger;
        _userSettings = userSettings;
        _connectionSettings = connectionSettings;
    }

    /// <inheritdoc />
    public SyncStatusDto GetStatus()
    {
        var user = _userSettings.CurrentValue;
        var defaults = _connectionSettings.Value;

        return new SyncStatusDto
        {
            AutoSyncEnabled = user.AutoSyncEnabled
                              ?? defaults.SyncInterval > TimeSpan.Zero,
            IntervalSeconds = (user.SyncInterval ?? defaults.SyncInterval).TotalSeconds,
            IsSyncing = _syncTrigger.IsSyncing,
        };
    }

    /// <inheritdoc />
    public async Task<SyncStatusDto> UpdateSettingsAsync(
        SyncSettingsUpdateDto dto, CancellationToken cancellationToken)
    {
        TimeSpan? interval = dto.IntervalSeconds.HasValue
            ? TimeSpan.FromSeconds(dto.IntervalSeconds.Value)
            : null;

        await _settingsService.UpdateAsync(new UserSettingsUpdate
        {
            AutoSyncEnabled = dto.AutoSyncEnabled,
            SyncInterval = interval,
        }, cancellationToken).ConfigureAwait(false);

        return GetStatus();
    }

    /// <inheritdoc />
    public bool TryTriggerSync()
    {
        if (_syncTrigger.IsSyncing)
        {
            return false;
        }

        _syncTrigger.TriggerSync();
        return true;
    }
}
