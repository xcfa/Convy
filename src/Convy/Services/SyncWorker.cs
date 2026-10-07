using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Convy.Services.Settings;
using Convy.Services.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services;

/// <summary>
/// Background loop that runs <see cref="SyncCycleService"/> on the configured interval and
/// on demand (<see cref="ISyncTrigger.TriggerSync"/>). A trigger that arrives while a cycle
/// is running is not lost: another cycle starts right after the current one.
/// </summary>
public sealed class SyncWorker : BackgroundService, ISyncTrigger
{
    private readonly SyncCycleService _cycle;
    private readonly IOptionsMonitor<UserSettings> _userSettings;
    private readonly IOptions<QBitTorrentConnectionSettings> _connectionSettings;
    private readonly ILogger<SyncWorker> _logger;

    // Wake-up signals for the loop. Capacity 1 with DropWrite coalesces bursts; whether a
    // wake-up must force a cycle is carried separately by _manualTriggerPending.
    private readonly Channel<bool> _wakeUps = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private int _manualTriggerPending;
    private volatile bool _isSyncing;
    private IDisposable? _settingsChangeRegistration;

    public SyncWorker(
        SyncCycleService cycle,
        IOptionsMonitor<UserSettings> userSettings,
        IOptions<QBitTorrentConnectionSettings> connectionSettings,
        ILogger<SyncWorker> logger)
    {
        _cycle = cycle;
        _userSettings = userSettings;
        _connectionSettings = connectionSettings;
        _logger = logger;
    }

    public bool IsSyncing => _isSyncing;

    public void TriggerSync()
    {
        Interlocked.Exchange(ref _manualTriggerPending, 1);
        _wakeUps.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Settings changes only wake the loop so the new interval takes effect.
        _settingsChangeRegistration = _userSettings.OnChange(_ => _wakeUps.Writer.TryWrite(true));

        var shouldSync = GetAutoSyncEnabled();

        while (!stoppingToken.IsCancellationRequested)
        {
            shouldSync |= Interlocked.Exchange(ref _manualTriggerPending, 0) == 1;

            if (shouldSync)
            {
                _isSyncing = true;
                try
                {
                    await _cycle.RunAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sync cycle failed.");
                }
                finally
                {
                    _isSyncing = false;
                }
            }

            try
            {
                await WaitForNextCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            shouldSync = GetAutoSyncEnabled();
        }
    }

    public override void Dispose()
    {
        _settingsChangeRegistration?.Dispose();
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

    /// <summary>Returns when the interval elapses or the loop is woken up.</summary>
    private async Task WaitForNextCycleAsync(CancellationToken stoppingToken)
    {
        if (Volatile.Read(ref _manualTriggerPending) == 1)
        {
            return;
        }

        var interval = GetSyncInterval();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (GetAutoSyncEnabled() && interval > TimeSpan.Zero)
        {
            timeout.CancelAfter(interval);
        }

        try
        {
            await _wakeUps.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Interval elapsed.
        }
    }
}
