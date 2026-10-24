using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Diagnostics;
using Convy.Services.Downloaders.Gallery;
using Convy.Services.Downloads;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Convy.Services;

/// <summary>
/// Downloads queued galleries one at a time (the sites rate-limit by IP; the pages of one gallery already go a few at
/// a time), and queues retries again when their time comes. Downloads that were running when Convy stopped start again
/// after a restart.
/// </summary>
public sealed class GalleryDownloadWorker : BackgroundService
{
    private static readonly TimeSpan RetryCheckInterval = TimeSpan.FromSeconds(30);

    private readonly IGalleryDownloadStore _store;
    private readonly GalleryDownloadQueue _queue;
    private readonly GalleryDownloadRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GalleryDownloadWorker> _logger;

    public GalleryDownloadWorker(
        IGalleryDownloadStore store,
        GalleryDownloadQueue queue,
        GalleryDownloadRunner runner,
        TimeProvider timeProvider,
        ILogger<GalleryDownloadWorker> logger)
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

    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        foreach (var download in await _store.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (download.State == GalleryDownloadState.Downloading)
            {
                await _store.TryUpdateAsync(download with { State = GalleryDownloadState.Queued }, download.State, cancellationToken).ConfigureAwait(false);
                _queue.Enqueue(download.ItemRef);
            }
            else if (download.State == GalleryDownloadState.Queued)
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
            using var scope = LogScopes.Item(_logger, DownloadProviders.Gallery, itemRef);
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

    private async Task RetryAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RetryCheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var now = _timeProvider.GetUtcNow();
                var due = (await _store.ListAsync(stoppingToken).ConfigureAwait(false))
                    .Where(d => d.State == GalleryDownloadState.Retrying && d.NextAttemptAt <= now);
                foreach (var download in due)
                {
                    if (await _store.TryUpdateAsync(download with { State = GalleryDownloadState.Queued }, download.State, stoppingToken).ConfigureAwait(false))
                    {
                        _queue.Enqueue(download.ItemRef);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not queue gallery downloads for a retry.");
            }
        }
    }
}
