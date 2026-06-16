using Banned.Qbittorrent;
using Convy.Data.Context;
using Convy.Data.Entities;
using Convy.Infrastructure.Helpers;
using Convy.Services.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Services
{
    /// <summary>
    /// Owns the qBittorrent connection and performs one sync cycle on demand:
    /// pulls the incremental main data, asks the state tracker what changed, and
    /// hard-links the files of newly-downloaded torrents into their routed output
    /// directories. This holds the business logic that used to live in the
    /// <c>BackgroundService</c>.
    /// </summary>
    public sealed class QBitTorrentCommunicationService
    {
        private readonly QBitTorrentConnectionSettings _settings;
        private readonly IDbContextFactory<ConvyDbContext> _dbFactory;
        private readonly OutputDirectoryMatcher _outputMatcher;
        private readonly ITorrentStateTracker _tracker;
        private readonly ILogger<QBitTorrentCommunicationService> _logger;

        private QBittorrentClient? _client;
        private int _rid;

        public QBitTorrentCommunicationService(
            IOptions<QBitTorrentConnectionSettings> settings,
            IDbContextFactory<ConvyDbContext> dbFactory,
            OutputDirectoryMatcher outputMatcher,
            ITorrentStateTracker tracker,
            ILogger<QBitTorrentCommunicationService> logger)
        {
            _settings = settings.Value;
            _dbFactory = dbFactory;
            _outputMatcher = outputMatcher;
            _tracker = tracker;
            _logger = logger;
        }

        public async Task SyncFilesAsync(CancellationToken cancellationToken)
        {
            var client = await EnsureConnectedAsync();

            var mainData = await client.Sync.GetMainData(_rid);

            if (mainData == null)
            {
	            throw new Exception("Client returned empty response");
            }

            _rid = mainData.Rid;

            var changes = await _tracker.ApplyAsync(mainData, cancellationToken);

            if (changes.Count == 0)
            {
	            return;
			}

            _logger.LogInformation("{Count} torrent change(s) to process.", changes.Count);

            await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var processed = new List<string>();

            foreach (var hash in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (await ProcessChangeAsync(client, context, hash, cancellationToken))
                    {
                        processed.Add(hash);
                    }
                }
                catch (Exception ex)
                {
                    // Isolate per torrent: one failure must neither abort the batch nor
                    // advance this torrent's baseline. It will be retried next cycle.
                    _logger.LogError(ex, "Failed to process torrent {Hash}.", hash);
                }
            }

            await context.SaveChangesAsync(cancellationToken);

            // Only now, after the file links are durably recorded, advance the tracker
            // baseline for the torrents that were fully handled.
            await _tracker.ConfirmProcessedAsync(processed, cancellationToken);
        }

        private async Task<QBittorrentClient> EnsureConnectedAsync()
        {
	        if (_client is not null)
	        {
		        return _client;
	        }

            var client = await QBittorrentClient.Create(_settings.Url, _settings.Username, _settings.Password ?? string.Empty);
            await client.Authentication.Login();
            _client = client;

            return client;
        }

        /// <summary>
        /// Links every not-yet-linked file of the torrent into its routed directory.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the torrent is fully handled (all files linked, or no rule
        /// matched). <c>false</c> when work remains (content missing, link failed, …),
        /// so the change is retried on a later cycle.
        /// </returns>
        private async Task<bool> ProcessChangeAsync(
            QBittorrentClient client,
            ConvyDbContext context,
            string hash,
            CancellationToken cancellationToken)
        {
            var info = await client.Torrent.GetTorrentInfo(hash);
            if (info is null)
            {
                _logger.LogWarning("Torrent {Hash} info unavailable; will retry.", hash);
                return false;
            }

            info.Hash = hash;

            var targetPath = _outputMatcher.Match(info);
            if (targetPath is null)
            {
                _logger.LogDebug("No mapping rule matched torrent {Hash}; nothing to do.", hash);
                return true;
            }

            if (string.IsNullOrEmpty(info.SavePath))
            {
                _logger.LogWarning("Torrent {Hash} has no SavePath yet; will retry.", hash);
                return false;
            }

            _logger.LogInformation("Torrent {Hash} -> {Target}", hash, targetPath);

            var alreadyLinked = await context.FileEntries
                .Where(x => x.InfoHash == hash)
                .Select(x => x.FilePath)
                .ToListAsync(cancellationToken);
            var linked = alreadyLinked.ToHashSet();

            var files = await client.Torrent.GetTorrentFiles(hash);
            if (files is null)
            {
                _logger.LogWarning("Torrent {Hash} file list unavailable; will retry.", hash);
                return false;
            }

            var allLinked = true;

            foreach (var file in files)
            {
                if (linked.Contains(file.Name))
                {
                    continue;
                }

                var source = Path.Combine(info.SavePath, file.Name);
                var dest = Path.Combine(targetPath, file.Name);

                // The link already exists on disk but wasn't recorded (e.g. a crash
                // between linking and saving). Record it instead of re-linking, which
                // would fail with EEXIST.
                if (File.Exists(dest))
                {
	                _logger.LogDebug("Destination already exists: {Destination}. Only added to database.", dest);
					RecordLink(context, hash, file.Name, dest);
                    linked.Add(file.Name);
                    continue;
                }

                if (!File.Exists(source))
                {
                    // Content not on disk yet (e.g. output not mounted, or size changed
                    // mid-download). Retry on a later cycle once the file appears.
                    _logger.LogDebug("Source not present yet, will retry: {Source}", source);
                    allLinked = false;
                    continue;
                }

                try
                {
                    var destDir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    NativeLink.CreateHardLink(source, dest);
                    RecordLink(context, hash, file.Name, dest);
                    linked.Add(file.Name);

                    _logger.LogInformation("Linked {Source} -> {Dest}", source, dest);
                }
                catch (Exception ex)
                {
                    // e.g. destination directory not mounted. Don't mark handled.
                    _logger.LogError(ex, "Failed to link {Source} -> {Dest}; will retry.", source, dest);
                    allLinked = false;
                }
            }

            return allLinked;
        }

        private static void RecordLink(ConvyDbContext context, string infoHash, string filePath, string dest) =>
            context.FileEntries.Add(new FileEntry
            {
                InfoHash = infoHash,
                FilePath = filePath,
                TargetPath = dest,
                LinkedDate = DateTimeOffset.Now,
            });
    }
}
