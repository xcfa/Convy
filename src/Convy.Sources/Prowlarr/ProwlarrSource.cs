using System.Runtime.CompilerServices;
using System.Text.Json;
using Convy.Sources.Torrents;
using Microsoft.Extensions.Logging;

namespace Convy.Sources.Prowlarr;

/// <summary>Offers one source per enabled torrent indexer of Prowlarr; all share one client.</summary>
public sealed class ProwlarrSourceProvider : ISourceProvider
{
    private readonly ProwlarrClient _client;
    private readonly ProwlarrOptions _options;
    private readonly ITorrentMetadataService _metadata;
    private readonly ILogger<ProwlarrSource> _sourceLogger;

    public ProwlarrSourceProvider(
        ProwlarrClient client,
        ProwlarrOptions options,
        ITorrentMetadataService metadata,
        ILogger<ProwlarrSource> sourceLogger)
    {
        _client = client;
        _options = options;
        _metadata = metadata;
        _sourceLogger = sourceLogger;
    }

    public string Name => "prowlarr";

    public async Task<IReadOnlyList<IContentSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return [];
        }

        var indexers = await _client.GetIndexersAsync(cancellationToken).ConfigureAwait(false);
        var blocked = (await _client.GetIndexerStatusesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.IndexerId);

        return indexers
            .Where(i => string.Equals(i.Protocol, "torrent", StringComparison.OrdinalIgnoreCase))
            .Select(i =>
            {
                var (status, message) = !i.Enable
                    ? (SourceStatus.Disabled, "Disabled in Prowlarr.")
                    : blocked.TryGetValue(i.Id, out var s)
                        ? (SourceStatus.Error, $"Failing; blocked until {s.DisabledTill:u}.")
                        : (SourceStatus.Ok, (string?)null);

                return (IContentSource)new ProwlarrSource(
                    i.Id, i.Name ?? $"Indexer {i.Id}", status, message, _client, _metadata, _sourceLogger);
            })
            .ToList();
    }
}

/// <summary>One Prowlarr indexer as a source (id <c>prowlarr:&lt;indexer id&gt;</c>).</summary>
public sealed class ProwlarrSource : IContentSource
{
    private readonly int _indexerId;
    private readonly ProwlarrClient _client;
    private readonly ITorrentMetadataService _metadata;
    private readonly ILogger<ProwlarrSource> _logger;

    public ProwlarrSource(
        int indexerId,
        string name,
        SourceStatus status,
        string? statusMessage,
        ProwlarrClient client,
        ITorrentMetadataService metadata,
        ILogger<ProwlarrSource> logger)
    {
        _indexerId = indexerId;
        Name = name;
        Status = status;
        StatusMessage = statusMessage;
        _client = client;
        _metadata = metadata;
        _logger = logger;
    }

    public string Id => $"prowlarr:{_indexerId}";

    public string Name { get; }

    public Protocol Protocol => Protocol.Torrent;

    public SourceStatus Status { get; }

    public string? StatusMessage { get; }

    public async IAsyncEnumerable<ContentInfo> SearchAsync(
        SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var releases = (await _client
                .SearchAsync(request.Query, _indexerId, request.Category.ProwlarrCategories, cancellationToken)
                .ConfigureAwait(false))
            .Where(r => r.Protocol is null || string.Equals(r.Protocol, "torrent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (releases.Count == 0)
        {
            // Prowlarr swallows indexer failures and answers with an empty list; a failing
            // indexer is blocked for at least a minute and shows up in indexerstatus.
            var statuses = await _client.GetIndexerStatusesAsync(cancellationToken).ConfigureAwait(false);
            if (statuses.FirstOrDefault(s => s.IndexerId == _indexerId) is { } failing)
            {
                throw new SourceException(
                    SourceErrorKind.Error,
                    $"Indexer is failing (last failure {failing.MostRecentFailure:u}, blocked until {failing.DisabledTill:u}); see the Prowlarr logs.");
            }

            yield break;
        }

        foreach (var release in releases)
        {
            var infoHash = MagnetUri.NormalizeInfoHash(release.InfoHash);
            yield return new ContentInfo
            {
                ContentId = ProwlarrContentRef.Serialize(new ProwlarrContentRef(
                    release.Guid, release.Title, release.DownloadUrl, release.MagnetUrl, infoHash)),
                Title = release.Title ?? "(untitled)",
                SizeBytes = release.Size > 0 ? release.Size : null,
                FileCount = release.Files,
                DedupKey = infoHash is null ? null : $"btih:{infoHash}",
                Availability = new Availability { Seeders = release.Seeders, Leechers = release.Leechers },
            };
        }
    }

    public async Task<FileListing> ListFilesAsync(string contentId, CancellationToken cancellationToken)
    {
        var reference = ProwlarrContentRef.Deserialize(contentId);
        var download = await DownloadAsync(reference, cancellationToken).ConfigureAwait(false);

        var metadata = download.TorrentFile is { } torrentFile
            ? Parse(torrentFile)
            : await _metadata.FetchAsync(download.Magnet!, cancellationToken).ConfigureAwait(false);

        return new FileListing(metadata.Files, metadata.TorrentFile);
    }

    public async Task<DownloadPayload> ResolveAsync(string contentId, CancellationToken cancellationToken)
    {
        var reference = ProwlarrContentRef.Deserialize(contentId);
        var download = await DownloadAsync(reference, cancellationToken).ConfigureAwait(false);

        if (download.TorrentFile is { } torrentFile)
        {
            return new TorrentPayload(Parse(torrentFile).InfoHash, null, torrentFile);
        }

        var magnet = download.Magnet!;
        var infoHash = MagnetUri.GetInfoHash(magnet) ?? reference.InfoHash
                       ?? throw new SourceException(SourceErrorKind.Error, "The magnet link has no info hash.");
        return new TorrentPayload(infoHash, magnet, null);
    }

    /// <summary>
    /// Resolves the release through the Prowlarr proxy (which applies the indexer's cookies
    /// and credentials): the .torrent link first, then the magnet link, then the bare hash.
    /// </summary>
    private async Task<ProwlarrDownload> DownloadAsync(ProwlarrContentRef reference, CancellationToken cancellationToken)
    {
        SourceException? failure = null;

        foreach (var url in new[] { reference.DownloadUrl, reference.MagnetUrl })
        {
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            try
            {
                return await _client.DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceException ex) when (ex.Kind == SourceErrorKind.Error)
            {
                _logger.LogWarning("Prowlarr download link of '{Title}' failed: {Message}", reference.Title, ex.Message);
                failure = ex;
            }
        }

        if (reference.InfoHash is { } infoHash)
        {
            return new ProwlarrDownload(null, MagnetUri.Create(infoHash, reference.Title));
        }

        throw failure ?? new SourceException(SourceErrorKind.Error, "The release has no download link.");
    }

    private TorrentMetadata Parse(byte[] torrentFile)
    {
        try
        {
            return _metadata.Parse(torrentFile);
        }
        catch (InvalidDataException ex)
        {
            throw new SourceException(SourceErrorKind.Error, "The indexer returned an invalid .torrent file.", ex);
        }
    }
}

/// <summary>What Convy keeps about a Prowlarr release to list and download it later.</summary>
public sealed record ProwlarrContentRef(string? Guid, string? Title, string? DownloadUrl, string? MagnetUrl, string? InfoHash)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(ProwlarrContentRef reference) => JsonSerializer.Serialize(reference, Json);

    public static ProwlarrContentRef Deserialize(string contentId) =>
        JsonSerializer.Deserialize<ProwlarrContentRef>(contentId, Json)
        ?? throw new SourceException(SourceErrorKind.Error, "Corrupted content reference.");
}
