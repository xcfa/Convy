namespace Convy.Services.Downloads;

/// <summary>Coarse, downloader-independent state of a download.</summary>
public enum DownloadState
{
    /// <summary>Added but not transferring yet (client queue, peer queue, fetching metadata).</summary>
    Queued,

    /// <summary>Transferring (or trying to: a torrent without seeds is still "downloading").</summary>
    Downloading,

    /// <summary>Stopped by the user or by Convy.</summary>
    Paused,

    /// <summary>Every wanted file is on disk.</summary>
    Completed,

    /// <summary>The downloader reports an error or the transfer was rejected.</summary>
    Failed,

    /// <summary>The downloader reports a state Convy does not map (e.g. moving files).</summary>
    Unknown,
}

/// <summary>A file of a download.</summary>
/// <param name="Path">Path relative to <see cref="DownloadItem.SavePath"/>, '/'-separated.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Progress">Completion between 0 and 1.</param>
/// <param name="Selected">
/// <c>false</c> when the file is excluded from the download (qBittorrent priority 0, or a
/// Soulseek file that was not queued). Unselected files are never placed.
/// </param>
public sealed record DownloadFile(string Path, long Size, double Progress, bool Selected)
{
    /// <summary>Whether the file is selected and fully downloaded.</summary>
    public bool IsComplete => Selected && Math.Abs(Progress - 1) < 0.001;
}

/// <summary>
/// One download as seen by a downloader, shared by all downloaders. This is what the sync
/// worker places and what the rules are evaluated against (<see cref="Properties"/>).
/// </summary>
public sealed record DownloadItem
{
    /// <summary>The downloader that owns the item (<see cref="IDownloader.Provider"/>).</summary>
    public required string Provider { get; init; }

    /// <summary>Stable reference of the item inside its downloader (the info hash for torrents).</summary>
    public required string ItemRef { get; init; }

    /// <summary>Human-readable name (torrent name, Soulseek folder name).</summary>
    public required string Name { get; init; }

    /// <summary>Directory the downloader writes into; <see cref="DownloadFile.Path"/> is relative to it.</summary>
    public required string SavePath { get; init; }

    public required DownloadState State { get; init; }

    /// <summary>Total size of the wanted content in bytes, when known.</summary>
    public long? Size { get; init; }

    /// <summary>Bytes of the wanted content already on disk.</summary>
    public long Downloaded { get; init; }

    /// <summary>Current download speed in bytes per second, when known.</summary>
    public long? DownloadSpeed { get; init; }

    /// <summary>Error reported by the downloader, if any.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// The item's files. Populated by <see cref="IDownloader.GetItemAsync"/>; list views
    /// (<see cref="IDownloader.GetItemsAsync"/>) may leave it empty when listing files is costly.
    /// </summary>
    public IReadOnlyList<DownloadFile> Files { get; init; } = [];

    /// <summary>
    /// Properties visible to the routing rules, keyed by canonical rule property name.
    /// A property the downloader does not know about is absent; a known but unset one is <c>null</c>.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Properties { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the downloader finished every wanted file.</summary>
    public bool IsDownloaded => State == DownloadState.Completed;
}
