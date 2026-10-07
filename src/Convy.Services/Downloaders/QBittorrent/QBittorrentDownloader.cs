using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Torrent;
using Convy.Services.Downloads;
using Convy.Sources;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Downloaders.QBittorrent;

/// <summary>
/// qBittorrent behind <see cref="IDownloader"/>. Item references are lower-case info hashes.
/// <see cref="GetItemsAsync"/> keeps a full picture of all torrents by merging qBittorrent's
/// incremental sync responses, so a sync cycle costs one request plus one per changed torrent.
/// </summary>
public sealed class QBittorrentDownloader : IDownloader, IDisposable
{
    /// <summary>How long <see cref="AddAsync"/> waits for qBittorrent to register a new torrent.</summary>
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RegistrationPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IQBittorrentApi _api;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<QBittorrentDownloader> _logger;

    // Incremental-sync state: guarded by _syncGate (only GetItemsAsync touches it).
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly Dictionary<string, TorrentInfo> _torrents = new(StringComparer.OrdinalIgnoreCase);
    private int _rid;

    public QBittorrentDownloader(IQBittorrentApi api, TimeProvider timeProvider, ILogger<QBittorrentDownloader> logger)
    {
        _api = api;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Provider => DownloadProviders.QBittorrent;

    public Protocol Protocol => Protocol.Torrent;

    public async Task<string> AddAsync(
        DownloadPayload payload, FileSelection selection, AddOptions options, CancellationToken cancellationToken)
    {
        if (payload is not TorrentPayload torrent)
        {
            throw new ArgumentException($"qBittorrent cannot download a {payload.Protocol} payload.", nameof(payload));
        }

        var hash = torrent.InfoHash.ToLowerInvariant();

        if (await _api.GetTorrentInfoAsync(hash, cancellationToken).ConfigureAwait(false) is not null)
        {
            // Already in qBittorrent (added manually or by an earlier job): reuse it as is,
            // without touching its category or file priorities.
            _logger.LogInformation("Torrent {Hash} is already in qBittorrent; reusing it.", hash);
            return hash;
        }

        // With a file selection the torrent is added stopped, so no unwanted piece is
        // fetched before the priorities are applied. That needs the metadata up front.
        var stopped = !selection.IsAll;

        if (torrent.TorrentFile is { Length: > 0 } torrentFile)
        {
            await _api.AddTorrentFileAsync(torrentFile, options.Category, stopped, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(torrent.Magnet))
        {
            if (stopped)
            {
                throw new InvalidOperationException("Selecting files requires the torrent metadata, which is not available.");
            }

            await _api.AddMagnetAsync(torrent.Magnet, options.Category, stopped: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException("The torrent payload has neither a .torrent file nor a magnet link.", nameof(payload));
        }

        await WaitForRegistrationAsync(hash, cancellationToken).ConfigureAwait(false);

        if (stopped)
        {
            await ApplySelectionAsync(hash, selection, cancellationToken).ConfigureAwait(false);
            await _api.StartAsync(hash, cancellationToken).ConfigureAwait(false);
        }

        return hash;
    }

    public Task CancelAsync(string itemRef, CancellationToken cancellationToken) =>
        _api.StopAsync(itemRef, cancellationToken);

    public async Task<IReadOnlyList<DownloadItem>> GetItemsAsync(CancellationToken cancellationToken)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var mainData = await _api.GetMainDataAsync(_rid, cancellationToken).ConfigureAwait(false);

            if (mainData.FullUpdateEnabled == true)
            {
                // A full update is authoritative: anything not in it is gone.
                _torrents.Clear();
            }

            if (mainData.TorrentsRemoved is { } removed)
            {
                foreach (var hash in removed)
                {
                    _torrents.Remove(hash);
                }
            }

            if (mainData.Torrents is { } torrents)
            {
                foreach (var (hash, partial) in torrents)
                {
                    if (!_torrents.TryGetValue(hash, out var known))
                    {
                        known = new TorrentInfo();
                        _torrents[hash] = known;
                    }

                    TorrentInfoMerger.Merge(known, partial);
                    known.Hash = hash;
                }
            }

            _rid = mainData.Rid;

            return _torrents.Values.Select(info => ToItem(info, files: [])).ToList();
        }
        catch
        {
            // Start over with a full update next time rather than merging onto a stale base.
            _rid = 0;
            throw;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task<DownloadItem?> GetItemAsync(string itemRef, CancellationToken cancellationToken)
    {
        var info = await _api.GetTorrentInfoAsync(itemRef, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        info.Hash ??= itemRef;

        var files = await _api.GetTorrentFilesAsync(itemRef, cancellationToken).ConfigureAwait(false);
        if (files is null)
        {
            return null;
        }

        return ToItem(info, files.Select(ToFile).ToList());
    }

    public void Dispose() => _syncGate.Dispose();

    /// <summary>Maps a qBittorrent torrent state onto the downloader-independent state.</summary>
    public static DownloadState MapState(EnumTorrentState? state) => state switch
    {
        null => DownloadState.Unknown,
        var s when s.Value.IsDownloaded() => DownloadState.Completed,
        EnumTorrentState.Downloading or EnumTorrentState.ForcedDownload or EnumTorrentState.StalledDownload
            => DownloadState.Downloading,
        EnumTorrentState.MetaDownload or EnumTorrentState.QueuedDownload or EnumTorrentState.CheckingDownload
            or EnumTorrentState.Allocating or EnumTorrentState.CheckingResumeData
            => DownloadState.Queued,
        EnumTorrentState.StoppedDownload => DownloadState.Paused,
        EnumTorrentState.Error or EnumTorrentState.MissingFiles => DownloadState.Failed,
        _ => DownloadState.Unknown,
    };

    /// <summary>
    /// Matches qBittorrent's file names to selected result paths. Result paths are relative
    /// to the torrent root, while qBittorrent names may or may not include the root folder
    /// depending on its content layout, so both spellings are accepted.
    /// </summary>
    public static bool IsSelected(string qbittorrentName, IReadOnlySet<string> selectedPaths)
    {
        var name = qbittorrentName.Replace('\\', '/');
        if (selectedPaths.Contains(name))
        {
            return true;
        }

        var slash = name.IndexOf('/');
        return slash > 0 && selectedPaths.Contains(name[(slash + 1)..]);
    }

    private async Task WaitForRegistrationAsync(string hash, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + RegistrationTimeout;

        while (await _api.GetTorrentInfoAsync(hash, cancellationToken).ConfigureAwait(false) is null)
        {
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"qBittorrent did not register torrent {hash} within {RegistrationTimeout.TotalSeconds:0} s.");
            }

            await Task.Delay(RegistrationPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplySelectionAsync(string hash, FileSelection selection, CancellationToken cancellationToken)
    {
        var files = await _api.GetTorrentFilesAsync(hash, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"qBittorrent returned no file list for torrent {hash}.");

        var selected = new HashSet<string>(selection.Paths!, StringComparer.Ordinal);
        var unselected = new List<int>();
        var matched = 0;

        for (var position = 0; position < files.Count; position++)
        {
            var file = files[position];
            if (IsSelected(file.Name, selected))
            {
                matched++;
            }
            else
            {
                unselected.Add(file.Index ?? position);
            }
        }

        if (matched != selected.Count)
        {
            _logger.LogWarning(
                "Torrent {Hash}: {Matched} of {Selected} selected file(s) matched qBittorrent's file list.",
                hash, matched, selected.Count);
        }

        if (unselected.Count > 0)
        {
            await _api.SetFilesPriorityAsync(hash, unselected, EnumTorrentFilePriority.DoNotDownload, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private DownloadItem ToItem(TorrentInfo info, IReadOnlyList<DownloadFile> files)
    {
        var state = MapState(info.State);
        var properties = TorrentInfoProperties.ToProperties(info);

        return new DownloadItem
        {
            Provider = Provider,
            ItemRef = info.Hash ?? string.Empty,
            Name = info.Name ?? info.Hash ?? string.Empty,
            SavePath = info.SavePath ?? string.Empty,
            State = state,
            Size = info.Size,
            Downloaded = info.Completed ?? 0,
            DownloadSpeed = info.DownloadSpeed,
            Error = state == DownloadState.Failed ? $"qBittorrent reports state {info.State}." : null,
            Files = files,
            Properties = properties,
        };
    }

    private static DownloadFile ToFile(TorrentFileInfo file) =>
        new(file.Name.Replace('\\', '/'), file.Size, file.Progress, file.Priority != EnumTorrentFilePriority.DoNotDownload);
}
