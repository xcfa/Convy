using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Diagnostics;
using Convy.Services.Downloaders.Opds;
using Convy.Services.Downloads;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Convy.Services;

/// <summary>
/// Downloads queued books one at a time (catalogs such as Flibusta are slow and easily
/// overloaded), and queues retries again when their time comes. Downloads that were running
/// when Convy stopped start again after a restart.
/// </summary>
public sealed class OpdsDownloadWorker : BackgroundService
{
    private static readonly TimeSpan RetryCheckInterval = TimeSpan.FromSeconds(30);

    private readonly IOpdsDownloadStore _store;
    private readonly OpdsDownloadQueue _queue;
    private readonly OpdsDownloadRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpdsDownloadWorker> _logger;

    public OpdsDownloadWorker(
        IOpdsDownloadStore store,
        OpdsDownloadQueue queue,
        OpdsDownloadRunner runner,
        TimeProvider timeProvider,
        ILogger<OpdsDownloadWorker> logger)
    {
        _store = store;
        _queue = queue;
        _runner = runner;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ResumeAsync(stoppingToken).ConfigureAwait(false);
            await Task.WhenAll(DownloadAsync(stoppingToken), RetryAsync(stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Queues what was queued or running when Convy stopped.</summary>
    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        foreach (var download in await _store.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (download.State == OpdsDownloadState.Downloading)
            {
                await _store.TryUpdateAsync(download with { State = OpdsDownloadState.Queued }, download.State, cancellationToken)
                    .ConfigureAwait(false);
                _queue.Enqueue(download.ItemRef);
            }
            else if (download.State == OpdsDownloadState.Queued)
            {
                _queue.Enqueue(download.ItemRef);
            }
        }
    }

    private async Task DownloadAsync(CancellationToken stoppingToken)
    {
        await foreach (var itemRef in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            using var scope = LogScopes.Item(_logger, DownloadProviders.Opds, itemRef);
            _queue.Started(itemRef, stop);
            try
            {
                await _runner.RunAsync(itemRef, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Stopped downloading {Item}.", itemRef);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Downloading {Item} failed.", itemRef);
            }
            finally
            {
                _queue.Finished(itemRef);
            }
        }
    }

    /// <summary>Queues retrying downloads whose time has come.</summary>
    private async Task RetryAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RetryCheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var now = _timeProvider.GetUtcNow();
                var due = (await _store.ListAsync(stoppingToken).ConfigureAwait(false))
                    .Where(d => d.State == OpdsDownloadState.Retrying && d.NextAttemptAt <= now);

                foreach (var download in due)
                {
                    // Only one writer moves it back to the queue.
                    if (await _store.TryUpdateAsync(download with { State = OpdsDownloadState.Queued }, download.State, stoppingToken)
                            .ConfigureAwait(false))
                    {
                        _queue.Enqueue(download.ItemRef);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not queue book downloads for a retry.");
            }
        }
    }
}
