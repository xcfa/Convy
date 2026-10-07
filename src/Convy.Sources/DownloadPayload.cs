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

/// <summary>A remote Soulseek file.</summary>
/// <param name="Filename">Full remote file name, including the directory.</param>
/// <param name="Size">Size in bytes as reported by the peer.</param>
public sealed record SoulseekFile(string Filename, long Size);
