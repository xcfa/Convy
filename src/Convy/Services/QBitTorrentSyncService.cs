using System;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services;
using Convy.Services.Services;
using Convy.Services.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services;

public sealed class QBitTorrentSyncService : BackgroundService, ISyncTrigger, IDisposable
{
    private readonly QBitTorrentCommunicationService _communication;
    private readonly IOptionsMonitor<UserSettings> _userSettings;
    private readonly IOptions<QBitTorrentConnectionSettings> _connectionSettings;
    private readonly ILogger<QBitTorrentSyncService> _logger;

    private readonly object _gate = new();
    private CancellationTokenSource? _delayCts;
    private volatile bool _manualTriggerPending;
    private IDisposable? _settingsChangeRegistration;

    public bool IsSyncing { get; private set; }

    public QBitTorrentSyncService(
        QBitTorrentCommunicationService communication,
        IOptionsMonitor<UserSettings> userSettings,
        IOptions<QBitTorrentConnectionSettings> connectionSettings,
        ILogger<QBitTorrentSyncService> logger)
    {
        _communication = communication;
        _userSettings = userSettings;
        _connectionSettings = connectionSettings;
        _logger = logger;
    }

    public void TriggerSync()
    {
        _manualTriggerPending = true;
        WakeUp();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _settingsChangeRegistration = _userSettings.OnChange(_ => WakeUp());

        var shouldSync = GetAutoSyncEnabled();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (shouldSync)
            {
                IsSyncing = true;
                try
                {
                    await _communication.SyncFilesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "qBittorrent sync cycle failed.");
                }
                finally
                {
                    IsSyncing = false;
                }
            }

            var manualTrigger = await DelayAsync(stoppingToken).ConfigureAwait(false);
            shouldSync = manualTrigger || GetAutoSyncEnabled();
        }
    }

    public override void Dispose()
    {
        _settingsChangeRegistration?.Dispose();
        lock (_gate)
        {
            _delayCts?.Dispose();
            _delayCts = null;
        }
        base.Dispose();
    }

    private bool GetAutoSyncEnabled()
    {
        return _userSettings.CurrentValue.AutoSyncEnabled
               ?? _connectionSettings.Value.SyncInterval > TimeSpan.Zero;
    }

    private TimeSpan GetSyncInterval()
    {
        return _userSettings.CurrentValue.SyncInterval
               ?? _connectionSettings.Value.SyncInterval;
    }

    private async Task<bool> DelayAsync(CancellationToken stoppingToken)
    {
        _manualTriggerPending = false;

        CancellationTokenSource cts;
        lock (_gate)
        {
            _delayCts?.Dispose();
            _delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts = _delayCts;
        }

        try
        {
            var interval = GetSyncInterval();
            var autoEnabled = GetAutoSyncEnabled();
            var delay = autoEnabled && interval > TimeSpan.Zero
                ? interval
                : Timeout.InfiniteTimeSpan;
            await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return _manualTriggerPending;
        }
    }

    private void WakeUp()
    {
        lock (_gate) _delayCts?.Cancel();
    }
}
