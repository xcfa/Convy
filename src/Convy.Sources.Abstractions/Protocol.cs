namespace Convy.Sources;

/// <summary>
/// How a found result is transferred. The protocol selects the downloader that
/// handles the result; sources and downloaders never know about each other directly.
/// </summary>
public enum Protocol
{
    /// <summary>BitTorrent (magnet link or .torrent file).</summary>
    Torrent,

    /// <summary>Soulseek peer-to-peer file transfer.</summary>
    Soulseek,

    /// <summary>Plain HTTP download of books from an OPDS catalog (e.g. Flibusta).</summary>
    Opds,

    /// <summary>
    /// A gallery (a manga or comic release) downloaded page by page from a site and stored as one
    /// CBZ archive; the source implements <see cref="IGallerySource"/>.
    /// </summary>
    Gallery,
}
