using Banned.Qbittorrent.Models.Torrent;
using Convy.PathExpressions.Mappings;

namespace Convy.Services.Rules
{
    /// <summary>
    /// An immutable view of the routing rules at a point in time. <see cref="Version"/>
    /// increases every time the rules file is successfully reloaded, so consumers can
    /// detect a change without diffing the rules.
    /// </summary>
    public sealed class RulesSnapshot
    {
        public RulesSnapshot(ConvyMappings mappings, long version)
        {
            Mappings = mappings;
            Version = version;
        }

        public ConvyMappings Mappings { get; }

        public long Version { get; }

        /// <summary>The output path of the first matching rule, or <c>null</c>.</summary>
        public string? Resolve(TorrentInfo info) => Mappings.Resolve(info);
    }
}
