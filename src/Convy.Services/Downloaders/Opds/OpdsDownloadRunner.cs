using System.IO.Compression;
using Convy.Services.Media;
using Convy.Sources;
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

    /// <summary>The largest file of a book.</summary>
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly IOpdsDownloadStore _store;
    private readonly ISourceRegistry _sources;
    private readonly OpdsDownloadQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpdsDownloadRunner> _logger;

    public OpdsDownloadRunner(
        IOpdsDownloadStore store, ISourceRegistry sources, OpdsDownloadQueue queue, TimeProvider timeProvider, ILogger<OpdsDownloadRunner> logger)
    {
        _store = store;
        _sources = sources;
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

        if (await _sources.FindAsync(download.CatalogId, cancellationToken).ConfigureAwait(false) is not IFileSource source)
        {
            await UpdateAsync(
                    download with { State = OpdsDownloadState.Failed, Error = $"The source '{download.CatalogId}' is not installed or not enabled." },
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
                bytes = await DownloadFileAsync(source, download, file, cancellationToken).ConfigureAwait(false);
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
            string.Join(", ", download.WantedFiles.Select(f => f.Name)), source.Name, Path.Combine(download.SavePath, download.Folder));
    }

    private async Task<long> DownloadFileAsync(IFileSource source, OpdsDownload download, OpdsDownloadFile file, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(download.SavePath, download.Folder);
        var target = Path.Combine(folder, file.Name);
        // The names come from a source's package: never a path outside the download's own folder.
        var root = Path.GetFullPath(download.SavePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(target).StartsWith(root, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"The file {file.Name} of {download.Folder} would land outside {download.SavePath}.");
        }

        Directory.CreateDirectory(folder);
        var part = target + ".part";

        // A file may take a while (a large PDF, a slow catalog), but not forever.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter((source.Timeout ?? TimeSpan.FromMinutes(1)) * 3);

        try
        {
            await using (var output = new ProgressStream(
                             new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true),
                             total => _queue.ReportProgress(download.ItemRef, total)))
            {
                var (fits, _, mediaType) = await source.FetchFileAsync(new Uri(file.Url), output, MaxFileBytes, deadline.Token).ConfigureAwait(false);
                if (!fits)
                {
                    throw new IOException($"{file.Name} is larger than {MaxFileBytes / (1024 * 1024)} MB.");
                }

                if (mediaType is "text/html" or "application/xhtml+xml")
                {
                    throw new SourceException(SourceErrorKind.Error,
                        $"{source.Name} sent a web page instead of {file.Name} (it may still be converting the book, or the book was removed).");
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

    /// <summary>A stream that tells how much was written so far.</summary>
    private sealed class ProgressStream(Stream inner, Action<long> report) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Advance(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Advance(buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void Advance(int count)
        {
            _written += count;
            report(_written);
        }
    }
}
