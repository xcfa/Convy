using System.IO.Compression;
using Convy.Services.Media;
using Convy.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Downloaders.Gallery;

/// <summary>
/// Downloads one gallery: as the archive the site makes when the source has one for it, else page by page — lists its
/// pages through the source, fetches the images that are not on disk yet (a few at a time, each through the source's
/// HTTP) into <see cref="GalleryDownload.WorkPath"/>, then zips them into the CBZ. A failure is tried again later (up to <see cref="MaxAttempts"/> runs); pages already fetched are kept between runs.
/// </summary>
public sealed class GalleryDownloadRunner
{
    public const int MaxAttempts = 3;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly IGalleryDownloadStore _store;
    private readonly ISourceRegistry _sources;
    private readonly GalleryDownloadQueue _queue;
    private readonly IOptionsMonitor<GalleryOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GalleryDownloadRunner> _logger;

    public GalleryDownloadRunner(
        IGalleryDownloadStore store,
        ISourceRegistry sources,
        GalleryDownloadQueue queue,
        IOptionsMonitor<GalleryOptions> options,
        TimeProvider timeProvider,
        ILogger<GalleryDownloadRunner> logger)
    {
        _store = store;
        _sources = sources;
        _queue = queue;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Works on one queued download until its archive is complete, it waits for a retry, or it fails.
    /// <paramref name="cancellationToken"/> stops it: a cancelled download stays cancelled, any other stop (shutdown)
    /// leaves it queued for the next start.
    /// </summary>
    public async Task RunAsync(string itemRef, CancellationToken cancellationToken)
    {
        var download = await _store.GetAsync(itemRef, cancellationToken).ConfigureAwait(false);
        if (download is null || download.State != GalleryDownloadState.Queued)
        {
            return;
        }

        if (await _sources.FindAsync(download.SourceId, cancellationToken).ConfigureAwait(false) is not IGallerySource source)
        {
            await UpdateAsync(
                    download with { State = GalleryDownloadState.Failed, Error = $"The source '{download.SourceId}' is not available (its extension is not installed, or the source is disabled)." },
                    download.State)
                .ConfigureAwait(false);
            return;
        }

        var stored = download.State;
        download = download with { State = GalleryDownloadState.Downloading, NextAttemptAt = null };
        if (!await UpdateAsync(download, stored).ConfigureAwait(false))
        {
            return;
        }

        stored = GalleryDownloadState.Downloading;
        try
        {
            download = await DownloadAsync(source, download, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Unless it was cancelled, Convy is shutting down: carry on after the next start, from the pages saved so far.
            var latest = await _store.GetAsync(itemRef, CancellationToken.None).ConfigureAwait(false);
            await UpdateAsync((latest is { State: GalleryDownloadState.Downloading } ? latest : download) with { State = GalleryDownloadState.Queued }, stored).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is SourceException or HttpRequestException or IOException or InvalidDataException
                                       or OperationCanceledException or UnauthorizedAccessException or ProgressLost)
        {
            if (ex is ProgressLost)
            {
                return;
            }

            // The pages fetched before the failure were saved as they came: fail from that state, not the starting one.
            var latest = await _store.GetAsync(itemRef, CancellationToken.None).ConfigureAwait(false);
            await HandleFailureAsync(latest is { State: GalleryDownloadState.Downloading } ? latest : download,
                ex is OperationCanceledException ? "No data from the site in time." : ex.Message).ConfigureAwait(false);
            return;
        }

        if (!await UpdateAsync(download with { State = GalleryDownloadState.Completed, Error = null, Attempts = 0 }, stored).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogInformation("Downloaded {Title} ({Pages} pages) into {Path}.", download.Title, download.PagesDone, download.ArchivePath);
    }

    private async Task<GalleryDownload> DownloadAsync(IGallerySource source, GalleryDownload download, CancellationToken cancellationToken)
    {
        if (source.Manifest.Archives && await DownloadArchiveAsync(source, download, cancellationToken).ConfigureAwait(false) is { } archived)
        {
            return archived;
        }

        var pages = await source.GetPagesAsync(download.ContentId, cancellationToken).ConfigureAwait(false);
        if (pages.Count == 0)
        {
            throw new SourceException(SourceErrorKind.Error, "The gallery has no pages.");
        }

        Directory.CreateDirectory(download.WorkPath);
        download = download with { PagesTotal = pages.Count, PagesDone = CountDone(download.WorkPath, pages.Count), Bytes = SizeOf(download.WorkPath) };
        if (!await UpdateAsync(download, GalleryDownloadState.Downloading).ConfigureAwait(false))
        {
            throw new ProgressLost();
        }

        var options = _options.CurrentValue;
        var pending = pages.Where(p => Existing(download.WorkPath, p.Number) is null).OrderBy(p => p.Number).ToList();
        using var gate = new SemaphoreSlim(Math.Clamp(options.ParallelPages, 1, 8));
        // Pages finish in any order; the stored progress is written by one page at a time, so it never goes backwards.
        using var saving = new SemaphoreSlim(1, 1);
        var state = download;
        Exception? failure = null;

        // A failed page stops the run, but the pages already in flight finish: they are kept for the retry.
        await Task.WhenAll(pending.Select(async page =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref failure) is not null)
                {
                    return;
                }

                var bytes = await FetchPageAsync(source, state.WorkPath, page, TimeSpan.FromSeconds(Math.Max(10, options.PageTimeoutSeconds)), cancellationToken)
                    .ConfigureAwait(false);
                await saving.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    state = state with { PagesDone = state.PagesDone + 1, Bytes = state.Bytes + bytes };
                    if (!await UpdateAsync(state, GalleryDownloadState.Downloading).ConfigureAwait(false))
                    {
                        Interlocked.CompareExchange(ref failure, new ProgressLost(), null);
                    }
                }
                finally
                {
                    saving.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Interlocked.CompareExchange(ref failure, ex is OperationCanceledException
                    ? new SourceException(SourceErrorKind.Error, $"Page {page.Number}: no data from the site in time.")
                    : new SourceException(SourceErrorKind.Error, $"Page {page.Number}: {ex.Message}", ex), null);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            throw failure;
        }

        var size = await PackAsync(state, pages.Count, cancellationToken).ConfigureAwait(false);
        return state with { PagesDone = pages.Count, Bytes = size };
    }

    /// <summary>
    /// The archive the site makes, kept as it comes once it opens as a zip; <c>null</c> — the source has none for the
    /// release, or getting it failed (the pages are the way then). Pages a run before it fetched are dropped.
    /// </summary>
    private async Task<GalleryDownload?> DownloadArchiveAsync(IGallerySource source, GalleryDownload download, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(download.ArchivePath)!);
        var part = download.ArchivePath + ".part";
        GalleryArchive? archive;
        int pages;
        try
        {
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                archive = await source.DownloadArchiveAsync(download.ContentId, output, cancellationToken).ConfigureAwait(false);
            }

            if (archive is null)
            {
                TryDelete(part);
                return null;
            }

            if (archive.Bytes == 0)
            {
                throw new SourceException(SourceErrorKind.Error, "The site sent an empty archive.");
            }

            using (var zip = ZipFile.OpenRead(part))
            {
                pages = archive.Pages ?? zip.Entries.Count(e => ImageExtensions.Contains(Path.GetExtension(e.Name)));
            }

            File.Move(part, download.ArchivePath, overwrite: true);
        }
        catch (Exception ex) when (ex is SourceException or HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            TryDelete(part);
            _logger.LogWarning("{Title}: the site's archive failed ({Reason}); downloading the pages.", download.Title, ex.Message);
            return null;
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        try
        {
            if (Directory.Exists(download.WorkPath))
            {
                Directory.Delete(download.WorkPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; harmless.
        }

        return download with { PagesTotal = pages, PagesDone = pages, Bytes = new FileInfo(download.ArchivePath).Length };
    }

    /// <summary>Fetches one page's image into the work folder as <c>NNNN.ext</c>; a broken mirror is tried once more.</summary>
    private async Task<long> FetchPageAsync(IGallerySource source, string workPath, GalleryPage page, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var url = page.ImageUrl ?? await source.GetImageUrlAsync(page, deadline.Token).ConfigureAwait(false);
        try
        {
            return await SaveAsync(source, workPath, page.Number, url, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SourceException or HttpRequestException or IOException && !deadline.IsCancellationRequested)
        {
            var retry = await source.RetryImageUrlAsync(page, url, deadline.Token).ConfigureAwait(false);
            if (retry is null || retry == url)
            {
                throw;
            }

            _logger.LogWarning("Page {Page}: {Reason} Trying {Url}.", page.Number, ex.Message, retry);
            return await SaveAsync(source, workPath, page.Number, retry, deadline.Token).ConfigureAwait(false);
        }
    }

    private async Task<long> SaveAsync(IGallerySource source, string workPath, int number, Uri url, CancellationToken cancellationToken)
    {
        using var image = await source.OpenImageAsync(url, cancellationToken).ConfigureAwait(false);
        var extension = ExtensionOf(image.MediaType, url);
        var target = Path.Combine(workPath, $"{number:0000}{extension}");
        var part = target + ".part";
        try
        {
            long total;
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await image.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                total = output.Length;
            }

            if (total == 0)
            {
                throw new SourceException(SourceErrorKind.Error, "The site sent an empty image.");
            }

            if (image.Length is { } expected && expected != total)
            {
                throw new SourceException(SourceErrorKind.Error, $"The image is {total} bytes, the site announced {expected}.");
            }

            File.Move(part, target, overwrite: true);
            return total;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>Zips the pages (stored, not compressed: images do not shrink) into the archive, then removes the work folder.</summary>
    private static async Task<long> PackAsync(GalleryDownload download, int pages, CancellationToken cancellationToken)
    {
        var files = Enumerable.Range(1, pages)
            .Select(n => Existing(download.WorkPath, n) ?? throw new InvalidDataException($"Page {n} is missing on disk."))
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(download.ArchivePath)!);
        var part = download.ArchivePath + ".part";
        try
        {
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var file in files)
                {
                    var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.NoCompression);
                    await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await using var target = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(part, download.ArchivePath, overwrite: true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        try
        {
            Directory.Delete(download.WorkPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; harmless.
        }

        return new FileInfo(download.ArchivePath).Length;
    }

    private async Task HandleFailureAsync(GalleryDownload download, string reason)
    {
        var now = _timeProvider.GetUtcNow();
        var attempts = download.Attempts + 1;
        if (attempts < MaxAttempts)
        {
            var delay = RetryDelays[Math.Min(attempts - 1, RetryDelays.Length - 1)];
            _logger.LogWarning("{Title}: {Reason} Trying again in {Delay} min.", download.Title, reason, delay.TotalMinutes);
            await UpdateAsync(download with
            {
                State = GalleryDownloadState.Retrying, Error = reason, Attempts = attempts, NextAttemptAt = now + delay,
            }, GalleryDownloadState.Downloading).ConfigureAwait(false);
            return;
        }

        _logger.LogError("{Title} failed for good: {Reason}", download.Title, reason);
        await UpdateAsync(download with { State = GalleryDownloadState.Failed, Error = reason, Attempts = attempts }, GalleryDownloadState.Downloading)
            .ConfigureAwait(false);
    }

    /// <summary>Saves if the stored state is still <paramref name="expected"/>; <c>false</c> when a cancellation came first.</summary>
    private Task<bool> UpdateAsync(GalleryDownload download, GalleryDownloadState expected) =>
        _store.TryUpdateAsync(download with { UpdatedAt = _timeProvider.GetUtcNow() }, expected, CancellationToken.None);

    private static string? Existing(string workPath, int number) =>
        Directory.Exists(workPath)
            ? Directory.EnumerateFiles(workPath, $"{number:0000}.*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.Ordinal))
            : null;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".jxl", ".bmp",
    };

    private static int CountDone(string workPath, int pages) => Enumerable.Range(1, pages).Count(n => Existing(workPath, n) is not null);

    private static long SizeOf(string workPath) =>
        Directory.Exists(workPath)
            ? Directory.EnumerateFiles(workPath).Where(f => !f.EndsWith(".part", StringComparison.Ordinal)).Sum(f => new FileInfo(f).Length)
            : 0;

    private static string ExtensionOf(string? mediaType, Uri url)
    {
        var byType = mediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/avif" => ".avif",
            _ => null,
        };
        if (byType is not null)
        {
            return byType;
        }

        var fromUrl = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        return fromUrl is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".avif" ? (fromUrl == ".jpeg" ? ".jpg" : fromUrl) : ".jpg";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; overwritten by the next attempt.
        }
    }

    /// <summary>The stored state changed under the run (a cancellation): stop quietly.</summary>
    private sealed class ProgressLost : Exception;
}
