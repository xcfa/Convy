using System.Security.Cryptography;
using System.Text;
using Convy.Services.Diagnostics;
using Convy.Services.Downloads;
using Convy.Sources;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Downloaders.Gallery;

/// <summary>
/// Convy's own downloader for galleries from plugin sources (<see cref="Protocol.Gallery"/>). A gallery is stored as
/// <c>&lt;downloads_path&gt;/&lt;source&gt;/&lt;key&gt;/&lt;Title&gt;/&lt;Title&gt;.cbz</c>; the background worker fetches
/// its pages through the source and zips them, and the sync cycle places the archive like any other download (the
/// <c>Title</c> folder is the root a job's sub-path replaces). The same archive is what the REST API streams to Shizuka.
/// </summary>
public sealed class GalleryDownloader : IDownloader
{
    private readonly string _downloadsPath;
    private readonly IGalleryDownloadStore _store;
    private readonly GalleryDownloadQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GalleryDownloader> _logger;

    public GalleryDownloader(
        string downloadsPath, IGalleryDownloadStore store, GalleryDownloadQueue queue, TimeProvider timeProvider, ILogger<GalleryDownloader> logger)
    {
        _downloadsPath = downloadsPath;
        _store = store;
        _queue = queue;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Provider => DownloadProviders.Gallery;

    public Protocol Protocol => Protocol.Gallery;

    public async Task<string> AddAsync(DownloadPayload payload, FileSelection selection, AddOptions options, CancellationToken cancellationToken)
    {
        var gallery = Gallery(payload);
        if (!selection.IsAll)
        {
            throw new ConvyRequestException("A gallery is downloaded whole: it has no files to select.");
        }

        var itemRef = GetItemRef(gallery);
        var existing = await _store.GetAsync(itemRef, cancellationToken).ConfigureAwait(false);
        var folder = SafeName(gallery.Title);
        var download = new GalleryDownload
        {
            ItemRef = itemRef,
            SourceId = gallery.SourceId,
            ContentId = gallery.ContentId,
            Title = gallery.Title,
            Folder = folder,
            FileName = folder + ".cbz",
            SavePath = Path.Combine(_downloadsPath, SafeName(gallery.SourceId), Key(gallery.ContentId)),
            Category = options.Category,
            PagesTotal = gallery.PageCount,
            State = GalleryDownloadState.Queued,
            CreatedAt = existing?.CreatedAt ?? _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow(),
        };

        // A download of the same release that is running keeps running: a second record would race it for the folder.
        if (existing is { IsActive: true })
        {
            _logger.LogInformation("{Title} from {Source} is already being downloaded.", gallery.Title, gallery.SourceId);
            return itemRef;
        }

        // An archive of an earlier download that is still on disk is not fetched again.
        if (existing is { State: GalleryDownloadState.Completed } && File.Exists(existing.ArchivePath))
        {
            download = download with
            {
                Folder = existing.Folder, FileName = existing.FileName, SavePath = existing.SavePath,
                PagesTotal = existing.PagesTotal, PagesDone = existing.PagesDone, Bytes = existing.Bytes, State = GalleryDownloadState.Completed,
            };
        }

        using var scope = LogScopes.Item(_logger, Provider, itemRef);
        await _store.SaveAsync(download, cancellationToken).ConfigureAwait(false);
        if (download.State != GalleryDownloadState.Completed)
        {
            _queue.Enqueue(itemRef);
        }

        _logger.LogInformation("Queued {Title} from {Source}.", gallery.Title, gallery.SourceId);
        return itemRef;
    }

    public string GetItemRef(DownloadPayload payload)
    {
        var gallery = Gallery(payload);
        return $"{gallery.SourceId}:{gallery.ContentId}";
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

            var cancelled = download with { State = GalleryDownloadState.Cancelled, UpdatedAt = _timeProvider.GetUtcNow() };
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

    /// <remarks>The directory belongs to Convy, so it is created when missing.</remarks>
    public Task<IReadOnlyList<string>> GetDownloadDirectoriesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_downloadsPath);
        return Task.FromResult<IReadOnlyList<string>>([_downloadsPath]);
    }

    public Task<string?> GetDownloadDirectoryAsync(AddOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(_downloadsPath);

    /// <summary>The stored download behind an item, for the REST API (pages, the archive's path).</summary>
    public Task<GalleryDownload?> FindAsync(string itemRef, CancellationToken cancellationToken) => _store.GetAsync(itemRef, cancellationToken);

    private const string UnsafeChars = "/\\:*?\"<>|";

    /// <summary>
    /// A file-system safe name, the same on every platform: no path separators, none of <c>: * ? " &lt; &gt; |</c>, no
    /// control characters, trimmed, at most <see cref="MaxNameBytes"/> bytes of UTF-8 (Linux allows 255 per name, and
    /// <c>.cbz</c> is added), cut between characters, never inside one.
    /// </summary>
    public static string SafeName(string title)
    {
        var builder = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            builder.Append(UnsafeChars.Contains(c) || char.IsControl(c) ? '_' : c);
        }

        var name = builder.ToString().Trim().TrimEnd('.');
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
        {
            var bytes = 0;
            var length = 0;
            foreach (var rune in name.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > MaxNameBytes)
                {
                    break;
                }

                bytes += rune.Utf8SequenceLength;
                length += rune.Utf16SequenceLength;
            }

            name = name[..length].TrimEnd().TrimEnd('.');
        }

        return name.Length == 0 ? "gallery" : name;
    }

    /// <summary>The longest name in bytes of UTF-8: room for <c>.cbz</c> and <c>.part</c> under the 255 of Linux and ext4.</summary>
    public const int MaxNameBytes = 200;

    /// <summary>A short, file-system safe key of a content id (which may hold slashes).</summary>
    public static string Key(string contentId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contentId)))[..16];

    private DownloadItem ToItem(GalleryDownload download)
    {
        var complete = download.State == GalleryDownloadState.Completed;
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = download.Folder,
            ["Category"] = download.Category,
            ["SavePath"] = download.SavePath,
        };
        if (complete)
        {
            properties["Size"] = (double)download.Bytes;
        }

        return new DownloadItem
        {
            Provider = Provider,
            ItemRef = download.ItemRef,
            Name = download.Folder,
            SavePath = download.SavePath,
            State = download.State switch
            {
                GalleryDownloadState.Queued => DownloadState.Queued,
                GalleryDownloadState.Downloading => DownloadState.Downloading,
                GalleryDownloadState.Retrying => DownloadState.Errored,
                GalleryDownloadState.Completed => DownloadState.Completed,
                GalleryDownloadState.Failed => DownloadState.Failed,
                _ => DownloadState.Paused,
            },
            Size = complete ? download.Bytes : null,
            Downloaded = download.Bytes,
            Error = download.Error,
            Files = [new DownloadFile($"{download.Folder}/{download.FileName}", complete ? download.Bytes : 0, complete ? 1 : Progress(download), Selected: true)],
            Properties = properties,
        };
    }

    private static double Progress(GalleryDownload download) =>
        download.PagesTotal is { } total and > 0 ? Math.Clamp((double)download.PagesDone / total, 0, 0.99) : 0;

    private static GalleryPayload Gallery(DownloadPayload payload) =>
        payload as GalleryPayload ?? throw new ArgumentException($"The gallery downloader cannot download {payload.GetType().Name}.", nameof(payload));
}
