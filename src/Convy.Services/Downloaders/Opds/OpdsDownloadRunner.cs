using System.IO.Compression;
using Convy.Sources;
using Convy.Sources.Opds;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Downloaders.Opds;

/// <summary>
/// Downloads the wanted files of one book. A failed file is tried again later (up to
/// <see cref="MaxAttempts"/> times); when it fails for good and the book was added without a
/// file selection, the next preferred format takes its place (an EPUB the catalog cannot
/// convert falls back to FB2).
/// </summary>
public sealed class OpdsDownloadRunner
{
    public const int MaxAttempts = 3;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly IOpdsDownloadStore _store;
    private readonly OpdsCatalogs _catalogs;
    private readonly OpdsDownloadQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpdsDownloadRunner> _logger;

    public OpdsDownloadRunner(
        IOpdsDownloadStore store, OpdsCatalogs catalogs, OpdsDownloadQueue queue, TimeProvider timeProvider, ILogger<OpdsDownloadRunner> logger)
    {
        _store = store;
        _catalogs = catalogs;
        _queue = queue;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Works on one queued download until its files are complete, it waits for a retry, or it fails.
    /// <paramref name="cancellationToken"/> stops it: a cancelled download stays cancelled, any
    /// other stop (shutdown) leaves it queued for the next start.
    /// </summary>
    public async Task RunAsync(string itemRef, CancellationToken cancellationToken)
    {
        // Only queued downloads run: a retry is queued again when its time comes, so asking
        // twice never starts a download early or twice.
        var download = await _store.GetAsync(itemRef, cancellationToken).ConfigureAwait(false);
        if (download is null || download.State != OpdsDownloadState.Queued)
        {
            return;
        }

        if (_catalogs.Find(download.CatalogId) is not { } client)
        {
            await UpdateAsync(
                    download with { State = OpdsDownloadState.Failed, Error = $"The OPDS catalog '{download.CatalogId}' is not configured." },
                    download.State)
                .ConfigureAwait(false);
            return;
        }

        // Every save expects the state this run wrote last, so a cancellation always wins.
        var stored = download.State;
        download = download with { State = OpdsDownloadState.Downloading, NextAttemptAt = null };
        if (!await UpdateAsync(download, stored).ConfigureAwait(false))
        {
            return;
        }

        stored = OpdsDownloadState.Downloading;

        foreach (var file in download.WantedFiles.Where(f => !f.Done).ToList())
        {
            long bytes;
            try
            {
                bytes = await DownloadFileAsync(client, download, file, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Unless it was cancelled, Convy is shutting down: carry on after the next start.
                await UpdateAsync(download with { State = OpdsDownloadState.Queued }, stored).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is SourceException or HttpRequestException or IOException or InvalidDataException
                                           or OperationCanceledException or UnauthorizedAccessException)
            {
                var reason = ex is OperationCanceledException ? "No data from the catalog in time." : ex.Message;
                await HandleFailureAsync(download, file, reason).ConfigureAwait(false);
                return;
            }

            download = download with { Files = Replace(download.Files, file, file with { Done = true, Bytes = bytes }), Attempts = 0 };
            if (!await UpdateAsync(download, stored).ConfigureAwait(false))
            {
                return;
            }
        }

        if (!await UpdateAsync(download with { State = OpdsDownloadState.Completed, Error = null, Attempts = 0 }, stored).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogInformation(
            "Downloaded {Files} from {Catalog} into {Directory}.",
            string.Join(", ", download.WantedFiles.Select(f => f.Name)), client.Catalog.Name, Path.Combine(download.SavePath, download.Folder));
    }

    private async Task<long> DownloadFileAsync(OpdsClient client, OpdsDownload download, OpdsDownloadFile file, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(download.SavePath, download.Folder);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, file.Name);
        var part = target + ".part";

        // A file may take a while (a large PDF, a slow catalog), but not forever.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(client.Catalog.Timeout * 3);

        try
        {
            using (var response = await client.GetFileAsync(new Uri(file.Url), deadline.Token).ConfigureAwait(false))
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is "text/html" or "application/xhtml+xml")
                {
                    throw new SourceException(SourceErrorKind.Error,
                        $"{client.Catalog.Name} sent a web page instead of {file.Name} (it may still be converting the book, or the book was removed).");
                }

                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    total += read;
                    _queue.ReportProgress(download.ItemRef, total);
                }
            }

            if (file.Unzip)
            {
                await ExtractSingleAsync(part, target, deadline.Token).ConfigureAwait(false);
                File.Delete(part);
            }
            else
            {
                File.Move(part, target, overwrite: true);
            }

            return new FileInfo(target).Length;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>Stores the book inside a zip (an <c>.fb2</c> in a <c>.fb2.zip</c>) as <paramref name="target"/>.</summary>
    private static async Task ExtractSingleAsync(string zipPath, string target, CancellationToken cancellationToken)
    {
        await using var zip = await ZipFile.OpenReadAsync(zipPath, cancellationToken).ConfigureAwait(false);
        var extension = Path.GetExtension(target);
        var entry = zip.Entries
                        .Where(e => !string.IsNullOrEmpty(e.Name))
                        .OrderByDescending(e => e.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(e => e.Length)
                        .FirstOrDefault()
                    ?? throw new InvalidDataException("The archive is empty.");

        var unpacked = target + ".unzip";
        try
        {
            await using (var input = await entry.OpenAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(unpacked, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            File.Move(unpacked, target, overwrite: true);
        }
        catch
        {
            TryDelete(unpacked);
            throw;
        }
    }

    private async Task HandleFailureAsync(OpdsDownload download, OpdsDownloadFile file, string reason)
    {
        var now = _timeProvider.GetUtcNow();
        var attempts = download.Attempts + 1;

        if (attempts < MaxAttempts)
        {
            var delay = RetryDelays[Math.Min(attempts - 1, RetryDelays.Length - 1)];
            _logger.LogWarning("{File}: {Reason} Trying again in {Delay} min.", file.Name, reason, delay.TotalMinutes);
            await UpdateAsync(download with
            {
                State = OpdsDownloadState.Retrying,
                Error = reason,
                Attempts = attempts,
                NextAttemptAt = now + delay,
            }, OpdsDownloadState.Downloading).ConfigureAwait(false);
            return;
        }

        var next = download.Fallback && file.Preference is { } rank
            ? download.Files
                .Where(f => !f.Wanted && !f.Failed && f.Preference > rank)
                .OrderBy(f => f.Preference)
                .FirstOrDefault()
            : null;

        if (next is not null)
        {
            _logger.LogWarning("{File} failed for good ({Reason}); downloading {Next} instead.", file.Name, reason, next.Name);
            var files = Replace(download.Files, file, file with { Wanted = false, Failed = true });
            files = Replace(files, next, next with { Wanted = true });
            if (await UpdateAsync(download with
                {
                    Files = files,
                    State = OpdsDownloadState.Queued,
                    Error = $"{file.Name}: {reason} Trying {next.Name} instead.",
                    Attempts = 0,
                }, OpdsDownloadState.Downloading).ConfigureAwait(false))
            {
                _queue.Enqueue(download.ItemRef);
            }

            return;
        }

        _logger.LogError("{File} failed for good: {Reason}", file.Name, reason);
        await UpdateAsync(download with { State = OpdsDownloadState.Failed, Error = reason, Attempts = attempts }, OpdsDownloadState.Downloading)
            .ConfigureAwait(false);
    }

    /// <summary>Saves if the stored state is still <paramref name="expected"/>; <c>false</c> when a cancellation came first.</summary>
    private Task<bool> UpdateAsync(OpdsDownload download, OpdsDownloadState expected) =>
        _store.TryUpdateAsync(download with { UpdatedAt = _timeProvider.GetUtcNow() }, expected, CancellationToken.None);

    private static IReadOnlyList<OpdsDownloadFile> Replace(IReadOnlyList<OpdsDownloadFile> files, OpdsDownloadFile old, OpdsDownloadFile updated) =>
        files.Select(f => f.Name == old.Name ? updated : f).ToList();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left behind; overwritten by the next attempt.
        }
    }
}
