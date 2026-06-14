using Banned.Qbittorrent.Models.Torrent;

namespace Convy.PathExpressions.Properties;

/// <summary>
/// Describes one queryable torrent property: its canonical name, its
/// <see cref="PropertyKind"/>, and a typed accessor. Exactly one accessor is
/// populated, matching <see cref="Kind"/>.
/// </summary>
public sealed class TorrentProperty
{
    public required string Name { get; init; }
    public required PropertyKind Kind { get; init; }

    public Func<TorrentInfo, double?>? Number { get; init; }
    public Func<TorrentInfo, string?>? Text { get; init; }
    public Func<TorrentInfo, bool?>? Boolean { get; init; }
    public Func<TorrentInfo, IEnumerable<string>?>? Collection { get; init; }
}
