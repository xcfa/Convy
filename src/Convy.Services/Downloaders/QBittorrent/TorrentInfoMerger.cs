using System.Reflection;
using Banned.Qbittorrent.Models.Torrent;

namespace Convy.Services.Downloaders.QBittorrent;

/// <summary>
/// Applies a partial <see cref="TorrentInfo"/> from qBittorrent's incremental sync
/// (<c>/sync/maindata</c>) onto the last known full state. qBittorrent sends only the fields
/// that changed; an absent field deserializes as <c>null</c> and means "unchanged".
/// </summary>
public static class TorrentInfoMerger
{
    private static readonly PropertyInfo[] Properties = typeof(TorrentInfo)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToArray();

    /// <summary>Copies every non-null field of <paramref name="partial"/> onto <paramref name="target"/>.</summary>
    public static void Merge(TorrentInfo target, TorrentInfo partial)
    {
        foreach (var property in Properties)
        {
            var value = property.GetValue(partial);
            if (value is not null)
            {
                property.SetValue(target, value);
            }
        }
    }

    /// <summary>Creates an independent copy of <paramref name="source"/>.</summary>
    public static TorrentInfo Clone(TorrentInfo source)
    {
        var copy = new TorrentInfo();
        Merge(copy, source);
        return copy;
    }
}
