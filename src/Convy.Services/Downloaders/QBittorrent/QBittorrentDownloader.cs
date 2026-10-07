using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Torrent;
using Convy.Services.Downloads;
using Convy.Services.Placement;
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
            // Already in qBittorrent (added manually or by an earlier job): reuse it, keeping its
            // category, and only switch on the files this download needs.
            _logger.LogInformation("Torrent {Hash} is already in qBittorrent; reusing it.", hash);
            await EnableWantedFilesAsync(hash, selection, cancellationToken).ConfigureAwait(false);
            return hash;
        }

        // With a file selection the torrent is added stopped, so no unwanted piece is
        // fetched before the priorities are applied. That needs the metadata up front.
        var stopped = !selection.IsAll;

        if (torrent.TorrentFile is not { Length: > 0 } && string.IsNullOrEmpty(torrent.Magnet))
        {
            throw new ArgumentException("The torrent payload has neither a .torrent file nor a magnet link.", nameof(payload));
        }

        if (stopped && torrent.TorrentFile is not { Length: > 0 })
        {
            throw new InvalidOperationException("Selecting files requires the torrent metadata, which is not available.");
        }

        try
        {
            if (torrent.TorrentFile is { Length: > 0 } torrentFile)
            {
                await _api.AddTorrentFileAsync(torrentFile, options.Category, stopped, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _api.AddMagnetAsync(torrent.Magnet!, options.Category, stopped: false, cancellationToken).ConfigureAwait(false);
            }

            await WaitForRegistrationAsync(hash, cancellationToken).ConfigureAwait(false);

            if (stopped)
            {
                await ApplySelectionAsync(hash, selection, cancellationToken).ConfigureAwait(false);
                await _api.StartAsync(hash, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The torrent was not in qBittorrent before: take back a half-done add (stopped,
            // without its selection), so a retry starts clean instead of reusing it.
            await RemoveAfterFailedAddAsync(hash).ConfigureAwait(false);
            throw;
        }

        return hash;
    }

    public string GetItemRef(DownloadPayload payload) =>
        payload is TorrentPayload torrent
            ? torrent.InfoHash.ToLowerInvariant()
            : throw new ArgumentException($"qBittorrent cannot download a {payload.Protocol} payload.", nameof(payload));

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

    public async Task<IReadOnlyList<string>> GetDownloadDirectoriesAsync(CancellationToken cancellationToken)
    {
        var defaultPath = await _api.GetDefaultSavePathAsync(cancellationToken).ConfigureAwait(false);
        var categories = await _api.GetCategoriesAsync(cancellationToken).ConfigureAwait(false);

        return categories
            .Where(c => !string.IsNullOrWhiteSpace(c.SavePath))
            .Select(c => ResolveCategoryPath(defaultPath, c.SavePath))
            .Prepend(defaultPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<string?> GetDownloadDirectoryAsync(AddOptions options, CancellationToken cancellationToken)
    {
        var defaultPath = await _api.GetDefaultSavePathAsync(cancellationToken).ConfigureAwait(false);
        if (options.Category is null)
        {
            return defaultPath;
        }

        var categories = await _api.GetCategoriesAsync(cancellationToken).ConfigureAwait(false);
        var category = categories.FirstOrDefault(c => string.Equals(c.Name, options.Category, StringComparison.Ordinal));

        return string.IsNullOrWhiteSpace(category?.SavePath)
            ? defaultPath
            : ResolveCategoryPath(defaultPath, category.SavePath);
    }

    public void Dispose() => _syncGate.Dispose();

    /// <summary>qBittorrent resolves a relative category save path against the default save path.</summary>
    private static string ResolveCategoryPath(string defaultPath, string categoryPath) =>
        categoryPath.StartsWith('/') || Path.IsPathRooted(categoryPath)
            ? categoryPath
            : Path.Combine(defaultPath, categoryPath);

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
        // qBittorrent recovers from these (recheck, remount), so they are not final.
        EnumTorrentState.Error or EnumTorrentState.MissingFiles => DownloadState.Errored,
        _ => DownloadState.Unknown,
    };

    /// <summary>
    /// For a reused torrent: raises the priority of wanted files that are switched off (an
    /// earlier job may have skipped them) and starts it. Never switches a file off, so a
    /// selection made by the user or another job is not narrowed.
    /// </summary>
    private async Task EnableWantedFilesAsync(string hash, FileSelection selection, CancellationToken cancellationToken)
    {
        var files = await _api.GetTorrentFilesAsync(hash, cancellationToken).ConfigureAwait(false);
        if (files is null)
        {
            return;
        }

        var selected = selection.Paths is null ? null : new HashSet<string>(selection.Paths, StringComparer.Ordinal);
        var names = files.Select(f => f.Name.Replace('\\', '/')).ToList();
        var root = PlacementPlanner.FindRoot(names);

        var wanted = Enumerable.Range(0, files.Count)
            .Where(i => PlacementPlanner.IsInSelection(names[i], root, selected))
            .ToList();

        if (selected is not null && wanted.Count != selected.Count)
        {
            throw new InvalidOperationException(
                $"Only {wanted.Count} of the {selected.Count} selected file(s) were found in torrent {hash}.");
        }

        var switchedOff = wanted
            .Where(i => files[i].Priority == EnumTorrentFilePriority.DoNotDownload)
            .Select(i => files[i].Index ?? i)
            .ToList();

        if (switchedOff.Count > 0)
        {
            await _api.SetFilesPriorityAsync(hash, switchedOff, EnumTorrentFilePriority.Normal, cancellationToken).ConfigureAwait(false);
            await _api.StartAsync(hash, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes a torrent whose add failed half-way. Runs even when the request was cancelled,
    /// bounded by its own timeout; nothing is downloaded yet, and files are kept anyway.
    /// </summary>
    private async Task RemoveAfterFailedAddAsync(string hash)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _api.RemoveAsync(hash, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove torrent {Hash} after a failed add; remove it in qBittorrent.", hash);
        }
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
        var names = files.Select(f => f.Name.Replace('\\', '/')).ToList();

        // Result paths are relative to the torrent root, while qBittorrent names may or may
        // not include the root folder depending on its content layout.
        var root = PlacementPlanner.FindRoot(names);
        var unselected = new List<int>();
        var matched = 0;

        for (var position = 0; position < files.Count; position++)
        {
            if (PlacementPlanner.IsInSelection(names[position], root, selected))
            {
                matched++;
            }
            else
            {
                unselected.Add(files[position].Index ?? position);
            }
        }

        if (matched != selected.Count)
        {
            throw new InvalidOperationException(
                $"Only {matched} of the {selected.Count} selected file(s) were found in torrent {hash}.");
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
            Error = state == DownloadState.Errored ? $"qBittorrent reports state {info.State}." : null,
            Files = files,
            Properties = properties,
        };
    }

    private static DownloadFile ToFile(TorrentFileInfo file) =>
        new(file.Name.Replace('\\', '/'), file.Size, file.Progress, file.Priority != EnumTorrentFilePriority.DoNotDownload);
}
