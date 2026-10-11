namespace Convy.Sources;

/// <summary>
/// Everything a downloader needs to start a transfer, produced by the source that found
/// the result. Authorization, proxies and indexer specifics are already resolved, so the
/// downloader does not need to know where the result came from.
/// </summary>
/// <param name="Protocol">The protocol whose downloader accepts this payload.</param>
public abstract record DownloadPayload(Protocol Protocol);

/// <summary>A torrent to add, either as a magnet link or as raw .torrent bytes.</summary>
/// <param name="InfoHash">
/// Lower-case hex info hash as the torrent client identifies the torrent (the v1 hash, or
/// the truncated v2 hash for v2-only torrents).
/// </param>
/// <param name="Magnet">Magnet URI, when known.</param>
/// <param name="TorrentFile">Raw .torrent bytes, when known. Preferred over the magnet.</param>
public sealed record TorrentPayload(string InfoHash, string? Magnet, byte[]? TorrentFile)
    : DownloadPayload(Protocol.Torrent);

/// <summary>A folder shared by a Soulseek user.</summary>
/// <param name="Username">The peer sharing the files.</param>
/// <param name="Directory">The remote directory (Soulseek path, backslash-separated).</param>
/// <param name="Files">Files of the directory that may be queued.</param>
public sealed record SoulseekPayload(string Username, string Directory, IReadOnlyList<SoulseekFile> Files)
    : DownloadPayload(Protocol.Soulseek);

/// <summary>A book from an OPDS catalog: one file per format.</summary>
/// <param name="CatalogId">The catalog (<c>flibusta</c>), whose client also downloads the files.</param>
/// <param name="BookKey">Stable, file-system safe key of the book within the catalog.</param>
/// <param name="Folder">Folder the book's files are stored in, e.g. <c>Стругацкий - Пикник на обочине</c>.</param>
/// <param name="Files">Every format the catalog offers.</param>
public sealed record OpdsPayload(string CatalogId, string BookKey, string Folder, IReadOnlyList<OpdsFile> Files)
    : DownloadPayload(Protocol.Opds);

/// <summary>One format of an OPDS book.</summary>
/// <param name="Name">File name as stored, e.g. <c>Стругацкий - Пикник на обочине.epub</c>.</param>
/// <param name="Url">Absolute download address.</param>
/// <param name="Size">Size in bytes, when the catalog tells it.</param>
/// <param name="Unzip">The catalog sends the file zipped (<c>.fb2.zip</c>); the single file inside is stored instead.</param>
/// <param name="Preference">
/// Rank among the preferred formats (0 = most preferred), or <c>null</c> for other formats.
/// Without a file selection the best ranked one is downloaded, and the next one if it fails.
/// </param>
public sealed record OpdsFile(string Name, string Url, long? Size, bool Unzip, int? Preference);

/// <summary>A remote Soulseek file.</summary>
/// <param name="Filename">Full remote file name, including the directory.</param>
/// <param name="Size">Size in bytes as reported by the peer.</param>
public sealed record SoulseekFile(string Filename, long Size);
