using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Sync;
using Banned.Qbittorrent.Models.Torrent;

namespace Convy.Services.Downloaders.QBittorrent;

/// <summary>
/// The subset of the qBittorrent Web API that Convy uses. Owns the connection (login,
/// re-login) and isolates the client library, so the downloader logic is testable.
/// </summary>
public interface IQBittorrentApi
{
    /// <summary>Incremental sync (<c>/sync/maindata</c>); <paramref name="rid"/> 0 requests a full update.</summary>
    Task<MainData> GetMainDataAsync(int rid, CancellationToken cancellationToken);

    /// <summary>Full info of one torrent, or <c>null</c> when qBittorrent does not know it.</summary>
    Task<TorrentInfo?> GetTorrentInfoAsync(string hash, CancellationToken cancellationToken);

    /// <summary>The torrent's files, or <c>null</c> when qBittorrent does not know it.</summary>
    Task<IReadOnlyList<TorrentFileInfo>?> GetTorrentFilesAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Adds a torrent from raw .torrent bytes.</summary>
    Task AddTorrentFileAsync(byte[] torrentFile, string? category, bool stopped, CancellationToken cancellationToken);

    /// <summary>Adds a torrent from a magnet URI.</summary>
    Task AddMagnetAsync(string magnet, string? category, bool stopped, CancellationToken cancellationToken);

    /// <summary>Sets the download priority of the given file indexes.</summary>
    Task SetFilesPriorityAsync(string hash, IReadOnlyList<int> fileIndexes, EnumTorrentFilePriority priority, CancellationToken cancellationToken);

    /// <summary>Removes a torrent from qBittorrent, keeping its files.</summary>
    Task RemoveAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Stops (pauses) a torrent without removing it or its data.</summary>
    Task StopAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Starts (resumes) a torrent.</summary>
    Task StartAsync(string hash, CancellationToken cancellationToken);

    /// <summary>qBittorrent's default save path.</summary>
    Task<string> GetDefaultSavePathAsync(CancellationToken cancellationToken);

    /// <summary>Configured categories with their save paths.</summary>
    Task<IReadOnlyList<TorrentCategory>> GetCategoriesAsync(CancellationToken cancellationToken);
}
