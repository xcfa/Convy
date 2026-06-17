using Convy.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Linking
{
    /// <summary>The result of linking a torrent's files into its destination.</summary>
    /// <param name="NewlyLinked">
    /// File names that should now be recorded as linked (freshly linked, or found already
    /// present on disk and only needing a record).
    /// </param>
    /// <param name="MissingSources">Files whose source wasn't present yet.</param>
    /// <param name="AllLinked">
    /// <c>true</c> when every file is now linked (nothing missing, no link failure).
    /// </param>
    public readonly record struct LinkOutcome(IReadOnlyList<string> NewlyLinked, int MissingSources, bool AllLinked);

    /// <summary>
    /// Pure linking logic over an <see cref="IFileLinker"/>: hard-links the given files
    /// into their destination (skipping ones already present on disk). The caller decides
    /// which files still need linking; this has no dependency on qBittorrent or the
    /// database, so it is straightforward to unit-test with a fake linker.
    /// </summary>
    public sealed class FileLinkingService
    {
        private readonly IFileLinker _linker;
        private readonly ILogger<FileLinkingService> _logger;

        public FileLinkingService(IFileLinker linker, ILogger<FileLinkingService> logger)
        {
            _linker = linker;
            _logger = logger;
        }

        public LinkOutcome LinkFiles(
            string savePath,
            string targetPath,
            IEnumerable<string> fileNames)
        {
            var newlyLinked = new List<string>();
            var missingSources = 0;
            var allLinked = true;

            foreach (var name in fileNames)
            {
                var source = Path.Combine(savePath, name);
                var dest = Path.Combine(targetPath, name);

                // Already on disk but not recorded (e.g. a crash between linking and
                // saving). Record it instead of re-linking, which would fail with EEXIST.
                if (_linker.Exists(dest))
                {
                    _logger.LogDebug("Destination already exists, recording only: {Destination}", dest);
                    newlyLinked.Add(name);
                    continue;
                }

                if (!_linker.Exists(source))
                {
                    _logger.LogDebug("Source not present yet, will retry: {Source}", source);
                    missingSources++;
                    allLinked = false;
                    continue;
                }

                try
                {
                    _linker.Link(source, dest);
                    newlyLinked.Add(name);
                    _logger.LogInformation("Linked {Source} -> {Dest}", source, dest);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to link {Source} -> {Dest}; will retry.", source, dest);
                    allLinked = false;
                }
            }

            return new LinkOutcome(newlyLinked, missingSources, allLinked);
        }
    }
}
