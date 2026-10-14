using Convy.Services.Diagnostics;
using Convy.Services.Downloads;
using Convy.Sources;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Downloaders.Opds;

/// <summary>
/// Convy's own downloader for books from OPDS catalogs. A book is stored as
/// <c>&lt;downloads_path&gt;/&lt;catalog&gt;/&lt;book key&gt;/&lt;Author - Title&gt;/&lt;file&gt;</c>;
/// the background worker downloads it, and the sync cycle places it like any other download
/// (the <c>Author - Title</c> folder is the root a job's sub-path replaces).
/// </summary>
public sealed class OpdsDownloader : IDownloader
{
    private readonly string _downloadsPath;
    private readonly IOpdsDownloadStore _store;
    private readonly OpdsDownloadQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpdsDownloader> _logger;

    public OpdsDownloader(
        string downloadsPath, IOpdsDownloadStore store, OpdsDownloadQueue queue, TimeProvider timeProvider, ILogger<OpdsDownloader> logger)
    {
        _downloadsPath = downloadsPath;
        _store = store;
        _queue = queue;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Provider => DownloadProviders.Opds;

    public Protocol Protocol => Protocol.Opds;

    public async Task<string> AddAsync(DownloadPayload payload, FileSelection selection, AddOptions options, CancellationToken cancellationToken)
    {
        var book = Book(payload);
        var itemRef = GetItemRef(book);
        var existing = await _store.GetAsync(itemRef, cancellationToken).ConfigureAwait(false);
        var savePath = Path.Combine(_downloadsPath, book.CatalogId, book.BookKey);

        // Without a selection: the best ranked format (or the only one there is), with fallback.
        var best = book.Files.Where(f => f.Preference is not null).MinBy(f => f.Preference) ?? book.Files.FirstOrDefault();
        var wanted = selection.IsAll
            ? new HashSet<string>(best is null ? [] : [best.Name], StringComparer.Ordinal)
            : selection.Paths!.ToHashSet(StringComparer.Ordinal);

        var files = book.Files
            .Select(f =>
            {
                // A file of an earlier download that is still on disk is not fetched again.
                var earlier = existing?.Files.FirstOrDefault(e => e.Name == f.Name && e.Done);
                var onDisk = earlier is not null && File.Exists(Path.Combine(savePath, book.Folder, f.Name));
                return new OpdsDownloadFile(
                    f.Name, f.Url, f.Size, f.Unzip, f.Preference, wanted.Contains(f.Name), onDisk, onDisk ? earlier!.Bytes : 0);
            })
            .ToList();

        if (!files.Any(f => f.Wanted))
        {
            throw new ConvyRequestException("None of the selected files is a format of this book.");
        }

        using var scope = LogScopes.Item(_logger, Provider, itemRef);
        var now = _timeProvider.GetUtcNow();
        var complete = files.Where(f => f.Wanted).All(f => f.Done);
        await _store.SaveAsync(new OpdsDownload
        {
            ItemRef = itemRef,
            CatalogId = book.CatalogId,
            Folder = book.Folder,
            SavePath = savePath,
            Category = options.Category,
            Files = files,
            Fallback = selection.IsAll,
            State = complete ? OpdsDownloadState.Completed : OpdsDownloadState.Queued,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        if (!complete)
        {
            _queue.Enqueue(itemRef);
        }

        _logger.LogInformation("Queued {Folder} ({Files}).", book.Folder, string.Join(", ", files.Where(f => f.Wanted).Select(f => f.Name)));
        return itemRef;
    }

    public string GetItemRef(DownloadPayload payload)
    {
        var book = Book(payload);
        return $"{book.CatalogId}/{book.BookKey}";
    }

    public async Task CancelAsync(string itemRef, CancellationToken cancellationToken)
    {
        // The worker may be saving progress at the same moment; retry on its fresh state.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var download = await _store.GetAsync(itemRef, cancellationToken).ConfigureAwait(false);
            if (download is null || !download.IsActive)
            {
                break;
            }

            var cancelled = download with { State = OpdsDownloadState.Cancelled, UpdatedAt = _timeProvider.GetUtcNow() };
            if (await _store.TryUpdateAsync(cancelled, download.State, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        _queue.Stop(itemRef);
    }

    public async Task<IReadOnlyList<DownloadItem>> GetItemsAsync(CancellationToken cancellationToken)
    {
        var downloads = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return downloads.Select(ToItem).ToList();
    }

    public async Task<DownloadItem?> GetItemAsync(string itemRef, CancellationToken cancellationToken)
    {
        var download = await _store.GetAsync(itemRef, cancellationToken).ConfigureAwait(false);
        return download is null ? null : ToItem(download);
    }

    /// <remarks>The directory belongs to Convy, so it is created when missing (the storage check asks before the first download).</remarks>
    public Task<IReadOnlyList<string>> GetDownloadDirectoriesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_downloadsPath);
        return Task.FromResult<IReadOnlyList<string>>([_downloadsPath]);
    }

    public Task<string?> GetDownloadDirectoryAsync(AddOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(_downloadsPath);

    private DownloadItem ToItem(OpdsDownload download)
    {
        var wanted = download.WantedFiles.ToList();
        var live = download.State == OpdsDownloadState.Downloading ? _queue.LiveBytes(download.ItemRef) : 0;
        long? size = wanted.All(f => f.Done || f.Size is not null) ? wanted.Sum(f => f.Done ? f.Bytes : f.Size!.Value) : null;

        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = download.Folder,
            ["Category"] = download.Category,
            ["SavePath"] = download.SavePath,
        };
        if (size is { } known)
        {
            properties["Size"] = (double)known;
        }

        return new DownloadItem
        {
            Provider = Provider,
            ItemRef = download.ItemRef,
            Name = download.Folder,
            SavePath = download.SavePath,
            State = download.State switch
            {
                OpdsDownloadState.Queued => DownloadState.Queued,
                OpdsDownloadState.Downloading => DownloadState.Downloading,
                OpdsDownloadState.Retrying => DownloadState.Errored,
                OpdsDownloadState.Completed => DownloadState.Completed,
                OpdsDownloadState.Failed => DownloadState.Failed,
                _ => DownloadState.Paused,
            },
            Size = size,
            Downloaded = wanted.Where(f => f.Done).Sum(f => f.Bytes) + live,
            Error = download.Error,
            Files = wanted
                .Select(f => new DownloadFile($"{download.Folder}/{f.Name}", f.Done ? f.Bytes : f.Size ?? 0, f.Done ? 1 : 0, Selected: true))
                .ToList(),
            Properties = properties,
        };
    }

    private static OpdsPayload Book(DownloadPayload payload) =>
        payload as OpdsPayload ?? throw new ArgumentException($"The OPDS downloader cannot download {payload.GetType().Name}.", nameof(payload));
}
