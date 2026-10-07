using Convy.Sources;

namespace Convy.Services.Downloads;

/// <summary>
/// A download client (qBittorrent, slskd, …) behind a common interface. Downloaders know
/// nothing about where a payload came from; authorization and proxies are resolved by
/// the source before <see cref="AddAsync"/> is called.
/// </summary>
public interface IDownloader
{
    /// <summary>
    /// Provider name, e.g. <c>qbittorrent</c> or <c>slskd</c>. Exposed to the routing rules as
    /// the <c>Provider</c> property.
    /// </summary>
    string Provider { get; }

    /// <summary>The protocol whose payloads this downloader accepts.</summary>
    Protocol Protocol { get; }

    /// <summary>
    /// Starts downloading <paramref name="payload"/> right away (without waiting for a sync
    /// cycle) and returns the item reference used by <see cref="GetItemAsync"/>.
    /// </summary>
    Task<string> AddAsync(DownloadPayload payload, FileSelection selection, AddOptions options, CancellationToken cancellationToken);

    /// <summary>Stops the download. Downloaded data and created links are kept.</summary>
    Task CancelAsync(string itemRef, CancellationToken cancellationToken);

    /// <summary>
    /// Returns every item the downloader currently knows about. The list is authoritative:
    /// an item missing from it was removed from the downloader. Items may come without
    /// <see cref="DownloadItem.Files"/>; call <see cref="GetItemAsync"/> for those.
    /// </summary>
    Task<IReadOnlyList<DownloadItem>> GetItemsAsync(CancellationToken cancellationToken);

    /// <summary>Returns one item with its files, or <c>null</c> if the downloader does not know it.</summary>
    Task<DownloadItem?> GetItemAsync(string itemRef, CancellationToken cancellationToken);

    /// <summary>
    /// Directories the downloader writes completed files into, as paths that must also be
    /// valid inside Convy. Used to check that rule paths share their filesystem.
    /// </summary>
    Task<IReadOnlyList<string>> GetDownloadDirectoriesAsync(CancellationToken cancellationToken);

    /// <summary>The directory a download added with <paramref name="options"/> lands in, when known.</summary>
    Task<string?> GetDownloadDirectoryAsync(AddOptions options, CancellationToken cancellationToken);
}

/// <summary>Which files of a result to download.</summary>
/// <param name="Paths">
/// Selected file paths relative to the result root ('/'-separated), or <c>null</c> for all files.
/// </param>
public sealed record FileSelection(IReadOnlyCollection<string>? Paths)
{
    /// <summary>Download every file.</summary>
    public static FileSelection All { get; } = new((IReadOnlyCollection<string>?)null);

    /// <summary>Whether every file is selected.</summary>
    public bool IsAll => Paths is null;
}

/// <summary>Options applied when adding a download.</summary>
/// <param name="Category">Client category to assign (qBittorrent category); ignored by clients without categories.</param>
public sealed record AddOptions(string? Category);

/// <summary>Well-known provider names.</summary>
public static class DownloadProviders
{
    public const string QBittorrent = "qbittorrent";
    public const string Slskd = "slskd";
}
