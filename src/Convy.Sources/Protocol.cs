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
}
