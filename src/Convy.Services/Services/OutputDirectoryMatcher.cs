using Banned.Qbittorrent.Models.Torrent;
using Convy.PathExpressions.Mappings;

namespace Convy.Services.Services
{
    /// <summary>
    /// Resolves the output directory for a torrent by evaluating the rules loaded
    /// from ConvyMappings.ini, top to bottom (first match wins).
    /// </summary>
    public sealed class OutputDirectoryMatcher
    {
        private readonly ConvyMappings _mappings;

        public OutputDirectoryMatcher(ConvyMappings mappings) => _mappings = mappings;

        public static OutputDirectoryMatcher FromFile(string mappingsFilePath) =>
            new(ConvyMappings.LoadFromFile(mappingsFilePath));

        /// <summary>The destination path for <paramref name="info"/>, or <c>null</c> if no rule matches.</summary>
        public string? Match(TorrentInfo info) => _mappings.Resolve(info);
    }
}
