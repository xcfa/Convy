using Banned.Qbittorrent;
using Convy.Data.Context;
using Convy.Data.Entities;
using Convy.Services.Linking;
using Convy.Services.Rules;
using Convy.Services.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Services
{
    /// <summary>
    /// Owns the qBittorrent connection and performs one sync cycle on demand: refreshes
    /// the routing rules, pulls the incremental main data, asks the state tracker what
    /// changed, and hard-links the files of matching torrents into their destinations.
    /// </summary>
    public sealed class QBitTorrentCommunicationService
    {
        private readonly QBitTorrentConnectionSettings _settings;
        private readonly IDbContextFactory<ConvyDbContext> _dbFactory;
        private readonly IRulesProvider _rulesProvider;
        private readonly ITorrentStateTracker _tracker;
        private readonly FileLinkingService _linkingService;
        private readonly ILogger<QBitTorrentCommunicationService> _logger;

        private QBittorrentClient? _client;
        private int _rid;
        private long _lastRulesVersion;

        public QBitTorrentCommunicationService(
            IOptions<QBitTorrentConnectionSettings> settings,
            IDbContextFactory<ConvyDbContext> dbFactory,
            IRulesProvider rulesProvider,
            ITorrentStateTracker tracker,
            FileLinkingService linkingService,
            ILogger<QBitTorrentCommunicationService> logger)
        {
            _settings = settings.Value;
            _dbFactory = dbFactory;
            _rulesProvider = rulesProvider;
            _tracker = tracker;
            _linkingService = linkingService;
            _logger = logger;
        }

        public async Task SyncFilesAsync(CancellationToken cancellationToken)
        {
            var client = await EnsureConnectedAsync();

            // Capture an immutable rules snapshot once per cycle; a concurrent reload only
            // affects the next cycle, so there is no race with this run.
            var rules = _rulesProvider.GetCurrent();

            if (rules.Version != _lastRulesVersion)
            {
                // Rules changed: forget skipped torrents so they are re-emitted below and
                // re-evaluated against the new rules.
                await _tracker.ClearSkippedAsync(cancellationToken);
                _lastRulesVersion = rules.Version;
            }

            var mainData = await client.Sync.GetMainData(_rid);
            if (mainData is null)
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
            var skipped = new List<string>();

            foreach (var hash in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    switch (await ProcessChangeAsync(client, context, hash, rules, cancellationToken))
                    {
                        case ProcessOutcome.Handled:
                            processed.Add(hash);
                            break;

                        case ProcessOutcome.NoMatch:
                            skipped.Add(hash);
                            break;

                        case ProcessOutcome.Retry:
                            // Neither confirmed nor skipped -> re-emitted on a later cycle.
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // Isolate per torrent: one failure must neither abort the batch nor
                    // advance this torrent's baseline.
                    _logger.LogError(ex, "Failed to process torrent {Hash}.", hash);
                }
            }

            await context.SaveChangesAsync(cancellationToken);

            // Advance the tracker baseline only after the links are durably recorded.
            await _tracker.ConfirmProcessedAsync(processed, cancellationToken);
            await _tracker.MarkSkippedAsync(skipped, cancellationToken);
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

        private async Task<ProcessOutcome> ProcessChangeAsync(
            QBittorrentClient client,
            ConvyDbContext context,
            string hash,
            RulesSnapshot rules,
            CancellationToken cancellationToken)
        {
            var info = await client.Torrent.GetTorrentInfo(hash);
            if (info is null)
            {
                _logger.LogWarning("Torrent {Hash} info unavailable; will retry.", hash);
                return ProcessOutcome.Retry;
            }

            info.Hash = hash;

            var targetPath = rules.Resolve(info);
            if (targetPath is null)
            {
                _logger.LogDebug("No rule matched torrent {Hash}; marking skipped.", hash);
                return ProcessOutcome.NoMatch;
            }

            if (string.IsNullOrEmpty(info.SavePath))
            {
                _logger.LogWarning("Torrent {Hash} has no SavePath yet; will retry.", hash);
                return ProcessOutcome.Retry;
            }

            _logger.LogInformation("Torrent {Hash} -> {Target}", hash, targetPath);

            var alreadyLinked = (await context.FileEntries
                .Where(x => x.InfoHash == hash)
                .Select(x => x.FilePath)
                .ToListAsync(cancellationToken)).ToHashSet();

            var files = await client.Torrent.GetTorrentFiles(hash);
            if (files is null)
            {
                _logger.LogWarning("Torrent {Hash} file list unavailable; will retry.", hash);
                return ProcessOutcome.Retry;
            }

            // Only hand the linker the files we haven't linked yet.
            var pending = files.Select(f => f.Name).Where(name => !alreadyLinked.Contains(name));
            var outcome = _linkingService.LinkFiles(info.SavePath, targetPath, pending);

            foreach (var name in outcome.NewlyLinked)
            {
                context.FileEntries.Add(new FileEntry
                {
                    InfoHash = hash,
                    FilePath = name,
                    TargetPath = Path.Combine(targetPath, name),
                    LinkedDate = DateTimeOffset.Now,
                });
            }

            if (outcome.MissingSources > 0)
            {
                _logger.LogWarning(
                    "Torrent {Hash}: {Missing} file(s) not found under '{SavePath}' inside the container. " +
                    "Is qBittorrent's download path mounted here at the same absolute path? Will retry.",
                    hash, outcome.MissingSources, info.SavePath);
            }

            return outcome.AllLinked ? ProcessOutcome.Handled : ProcessOutcome.Retry;
        }

        private enum ProcessOutcome
        {
            /// <summary>Matched a rule and every file is linked.</summary>
            Handled,

            /// <summary>Matched no rule; skip until the rules change.</summary>
            NoMatch,

            /// <summary>Matched but work remains (missing content / link failure); retry later.</summary>
            Retry,
        }
    }
}
