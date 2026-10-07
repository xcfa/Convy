using Convy.Infrastructure.Helpers;
using Convy.Services.Placement;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Linking
{
    /// <summary>The result of linking an item's files into its destination.</summary>
    /// <param name="NewlyLinked">
    /// Source paths (relative to the save path) that should now be recorded as linked (freshly
    /// linked, or found already present on disk and only needing a record).
    /// </param>
    /// <param name="MissingSources">Files whose source wasn't present yet.</param>
    /// <param name="AllLinked">
    /// <c>true</c> when every file is now linked (nothing missing, no link failure).
    /// </param>
    /// <param name="Errors">Messages of failed link attempts.</param>
    public readonly record struct LinkOutcome(
        IReadOnlyList<string> NewlyLinked, int MissingSources, bool AllLinked, IReadOnlyList<string> Errors);

    /// <summary>
    /// Pure linking logic over an <see cref="IFileLinker"/>: hard-links the given files
    /// into their destination (skipping ones already present on disk). The caller decides
    /// which files still need linking; this has no dependency on a downloader or the
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

        /// <summary>Links each file to the same relative path under <paramref name="targetPath"/>.</summary>
        public LinkOutcome LinkFiles(
            string savePath,
            string targetPath,
            IEnumerable<string> fileNames) =>
            LinkPlanned(savePath, PlacementPlanner.PlanLinks(fileNames, targetPath, stripRoot: null));

        /// <summary>Creates the planned links; sources are relative to <paramref name="savePath"/>.</summary>
        public LinkOutcome LinkPlanned(string savePath, IEnumerable<PlannedLink> links)
        {
            var newlyLinked = new List<string>();
            var errors = new List<string>();
            var missingSources = 0;
            var allLinked = true;

            foreach (var (name, dest) in links)
            {
                var source = Path.Combine(savePath, name);

                // Already on disk but not recorded (e.g. a crash between linking and
                // saving). Record it instead of re-linking, which would fail with EEXIST.
                if (_linker.Exists(dest))
                {
                    _logger.LogWarning("Destination already exists, recording without linking: {Destination}", dest);
                    newlyLinked.Add(name);
                    continue;
                }

                if (!_linker.Exists(source))
                {
                    _logger.LogWarning("Source not present yet, will retry: {Source}", source);
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
                    errors.Add(ex.Message);
                    allLinked = false;
                }
            }

            return new LinkOutcome(newlyLinked, missingSources, allLinked, errors);
        }
    }
}
