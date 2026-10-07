using System.Net;
using BencodeNET.Objects;
using BencodeNET.Parsing;
using BencodeNET.Torrents;
using BencodeTorrent = BencodeNET.Torrents.Torrent;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace Convy.Sources.Torrents;

/// <summary>A torrent's identity and file list.</summary>
/// <param name="InfoHash">Lower-case hex v1 info hash.</param>
/// <param name="Name">Torrent name (root folder of a multi-file torrent).</param>
/// <param name="Files">Files relative to the torrent root; BEP 47 padding files are left out.</param>
/// <param name="TorrentFile">Complete .torrent bytes.</param>
public sealed record TorrentMetadata(string InfoHash, string Name, IReadOnlyList<ListedFile> Files, byte[] TorrentFile);

/// <summary>
/// Reads torrent metadata: parses .torrent files and fetches the metadata of magnet links
/// from the swarm (DHT and the magnet's trackers) without adding anything to the torrent client.
/// </summary>
public interface ITorrentMetadataService
{
    /// <summary>Parses .torrent bytes. Throws <see cref="InvalidDataException"/> for an invalid file.</summary>
    TorrentMetadata Parse(byte[] torrentFile);

    /// <summary>
    /// Fetches the metadata of a magnet link from peers. Runs until the metadata arrives or
    /// <paramref name="cancellationToken"/> is cancelled; the caller sets the deadline.
    /// </summary>
    Task<TorrentMetadata> FetchAsync(string magnet, CancellationToken cancellationToken);
}

/// <summary>Settings of the embedded metadata client.</summary>
public sealed class TorrentMetadataOptions
{
    /// <summary>Directory for the DHT routing cache (speeds up later lookups).</summary>
    public string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "convy-monotorrent");

    /// <summary>UDP port of the DHT node; 0 picks a free port.</summary>
    public int DhtPort { get; set; }
}

/// <inheritdoc cref="ITorrentMetadataService"/>
/// <remarks>
/// .torrent files are parsed with BencodeNET; magnet metadata is fetched with an embedded
/// MonoTorrent engine that is started on first use and only downloads metadata. It needs no
/// inbound port: lookups and peer connections are outgoing.
/// </remarks>
public sealed class TorrentMetadataService : ITorrentMetadataService, IAsyncDisposable
{
    private readonly TorrentMetadataOptions _options;
    private readonly ILogger<TorrentMetadataService> _logger;
    private readonly SemaphoreSlim _engineGate = new(1, 1);

    private ClientEngine? _engine;

    public TorrentMetadataService(TorrentMetadataOptions options, ILogger<TorrentMetadataService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public TorrentMetadata Parse(byte[] torrentFile)
    {
        BencodeTorrent torrent;
        try
        {
            torrent = new BencodeParser().Parse<BencodeTorrent>(torrentFile);
        }
        catch (Exception ex) when (ex is BencodeNET.Exceptions.BencodeException or InvalidCastException or FormatException)
        {
            throw new InvalidDataException("Not a valid .torrent file.", ex);
        }

        var files = torrent.FileMode == TorrentFileMode.Single
            ? new List<ListedFile> { new(torrent.File.FileName, torrent.File.FileSize) }
            : torrent.Files
                .Select(f => new ListedFile(string.Join('/', f.Path), f.FileSize))
                .Where(f => !IsPaddingFile(f.Path))
                .ToList();

        var infoHash = (torrent.OriginalInfoHash ?? torrent.GetInfoHash()).ToLowerInvariant();
        return new TorrentMetadata(infoHash, torrent.DisplayName ?? infoHash, files, torrentFile);
    }

    public async Task<TorrentMetadata> FetchAsync(string magnet, CancellationToken cancellationToken)
    {
        if (!MagnetLink.TryParse(magnet, out var link) || link is null)
        {
            throw new ArgumentException("Not a valid magnet link.", nameof(magnet));
        }

        var engine = await GetEngineAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await engine.DownloadMetadataAsync(link, cancellationToken).ConfigureAwait(false);

        // The engine completes with empty data instead of throwing when cancelled.
        cancellationToken.ThrowIfCancellationRequested();
        if (metadata.IsEmpty)
        {
            throw new InvalidDataException("No metadata was received.");
        }

        return Parse(ToTorrentFile(metadata.ToArray(), link));
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is { } engine)
        {
            try
            {
                await engine.StopAllAsync().ConfigureAwait(false);
            }
            finally
            {
                engine.Dispose();
            }
        }

        _engineGate.Dispose();
    }

    /// <summary>
    /// The engine may hand back either a complete torrent or just the info dictionary;
    /// returns complete .torrent bytes in both cases, adding the magnet's trackers.
    /// </summary>
    private static byte[] ToTorrentFile(byte[] metadata, MagnetLink link)
    {
        var dictionary = new BencodeParser().Parse<BDictionary>(metadata);
        if (dictionary.ContainsKey("info"))
        {
            return metadata;
        }

        var torrent = new BDictionary { ["info"] = dictionary };
        if (link.AnnounceUrls is { Count: > 0 } trackers)
        {
            torrent["announce"] = new BString(trackers[0]);
            torrent["announce-list"] = new BList(trackers.Select(t => new BList { new BString(t) }));
        }

        return torrent.EncodeAsBytes();
    }

    private static bool IsPaddingFile(string path) =>
        path.StartsWith(".pad/", StringComparison.Ordinal) || path.StartsWith("_____padding_file", StringComparison.Ordinal);

    private async Task<ClientEngine> GetEngineAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _engine) is { } existing)
        {
            return existing;
        }

        await _engineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine is not null)
            {
                return _engine;
            }

            Directory.CreateDirectory(_options.CacheDirectory);

            var settings = new EngineSettingsBuilder
            {
                AllowPortForwarding = false,
                AutoSaveLoadDhtCache = true,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = false,
                CacheDirectory = _options.CacheDirectory,
                DhtEndPoint = new IPEndPoint(IPAddress.Any, _options.DhtPort),
                ListenEndPoints = new Dictionary<string, IPEndPoint>(),
            }.ToSettings();

            _engine = new ClientEngine(settings);
            _logger.LogInformation("Torrent metadata client started (DHT port {Port}).", _options.DhtPort == 0 ? "auto" : _options.DhtPort.ToString());
            return _engine;
        }
        finally
        {
            _engineGate.Release();
        }
    }
}
