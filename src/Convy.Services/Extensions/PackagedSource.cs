using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Convy.Sources;
using Convy.Sources.Slskd;
using Convy.Sources.Torrents;
using Microsoft.Extensions.Logging;
using Shizuka.Sources.Hosting;
using Api = Shizuka.Sources.Abstractions;
using Content = Convy.Sources.Abstractions;

namespace Convy.Services.Extensions;

/// <summary>
/// A source of Convy's own API (<see cref="Content.ContentSource"/>: a torrent indexer, Soulseek, an OPDS catalog) as
/// Convy's source. Its calls get the context every extension source gets (<see cref="ExtensionUnit"/>); what it
/// returns becomes what Convy's downloaders take: a torrent's .torrent or magnet, a Soulseek folder, files Convy fetches
/// itself (<see cref="FetchFileAsync"/>). The files of a release the source does not list are read from the release.
/// </summary>
public sealed class PackagedSource : ExtensionUnit, IFileSource
{
    /// <summary>The largest .torrent Convy takes.</summary>
    private const long MaxTorrentBytes = 16L * 1024 * 1024;

    private const int MaxRedirects = 5;

    private readonly Content.ContentSource _source;

    /// <param name="settingsOf">The provider that listed the source; <c>null</c> — the package's own source.</param>
    public PackagedSource(Content.ContentSource source, string package, ExtensionHost host, ILogger logger, ExtensionUnit? settingsOf = null)
        : base(source.Manifest, package, host, logger, settingsOf)
    {
        _source = source;
    }

    public string Name => _source.Manifest.Name;

    public Protocol Protocol => _source.Protocol switch
    {
        Content.ContentProtocol.Torrent => Protocol.Torrent,
        Content.ContentProtocol.Soulseek => Protocol.Soulseek,
        _ => Protocol.Opds,
    };

    public SourceStatus Status { get; private set; } = SourceStatus.Ok;

    public string? StatusMessage { get; private set; }

    public TimeSpan? Timeout => _source.Timeout;

    /// <summary>Asks the source whether it can be used now; a failure to answer is the answer.</summary>
    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatusMessage = await CallAsync(_source.CheckAsync, cancellationToken, Timeout).ConfigureAwait(false);
        }
        catch (SourceException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            StatusMessage = $"{Name} did not answer in time.";
        }

        Status = StatusMessage is null ? SourceStatus.Ok : SourceStatus.Error;
    }

    public async IAsyncEnumerable<ContentInfo> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = new Content.ContentQuery
        {
            Text = request.Query,
            Category = request.Category.Id,
            Options = request.Category.For(Id, SettingsId),
        };
        var items = await CallAsync(async (context, ct) => await _source.SearchAsync(query, context, ct).ConfigureAwait(false) ?? [],
            cancellationToken, Timeout).ConfigureAwait(false);
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrEmpty(item.ContentId))
            {
                continue;
            }

            yield return new ContentInfo
            {
                ContentId = item.ContentId,
                Title = string.IsNullOrWhiteSpace(item.Title) ? "(untitled)" : item.Title,
                SizeBytes = item.SizeBytes,
                FileCount = item.FileCount,
                DedupKey = item.DedupKey,
                InfoUrl = item.InfoUrl is { Scheme: "http" or "https" } url ? url.AbsoluteUri : null,
                Availability = new Availability
                {
                    Seeders = item.Seeders,
                    Leechers = item.Leechers,
                    FreeUploadSlot = item.FreeUploadSlot,
                    QueueLength = item.QueueLength,
                    UploadSpeed = item.UploadSpeed,
                },
                Properties = item.Properties ?? new Dictionary<string, string>(),
            };
        }
    }

    public async Task<FileListing> ListFilesAsync(string contentId, CancellationToken cancellationToken)
    {
        var listed = await CallAsync((context, ct) => _source.ListFilesAsync(contentId, context, ct), cancellationToken, Timeout).ConfigureAwait(false);
        if (listed is not null)
        {
            return new FileListing(listed.Select(f => new ListedFile(f.Path, f.Size)).ToList());
        }

        switch (await ReleaseAsync(contentId, cancellationToken).ConfigureAwait(false))
        {
            case Content.TorrentRelease torrent:
                var metadata = await TorrentAsync(torrent, cancellationToken).ConfigureAwait(false);
                return new FileListing(metadata.Files, metadata.TorrentFile);
            case Content.SoulseekRelease folder:
                return new FileListing(folder.Files.Select(f => new ListedFile(SoulseekPaths.RelativeTo(folder.Directory, f.Path), f.Size)).ToList());
            case Content.FilesRelease files:
                return new FileListing(files.Files.Select(f => new ListedFile(f.Name, f.Size ?? 0)).ToList());
            default:
                throw Unknown();
        }
    }

    public async Task<DownloadPayload> ResolveAsync(string contentId, CancellationToken cancellationToken)
    {
        switch (await ReleaseAsync(contentId, cancellationToken).ConfigureAwait(false))
        {
            case Content.TorrentRelease torrent:
                var (bytes, magnet) = await TorrentLinkAsync(torrent, cancellationToken).ConfigureAwait(false);
                if (bytes is not null)
                {
                    return new TorrentPayload(Parse(bytes).InfoHash, null, bytes);
                }

                var infoHash = MagnetUri.GetInfoHash(magnet!) ?? MagnetUri.NormalizeInfoHash(torrent.InfoHash)
                               ?? throw new SourceException(SourceErrorKind.Error, "The magnet link has no info hash.");
                return new TorrentPayload(infoHash, magnet, null);
            case Content.SoulseekRelease folder:
                return new SoulseekPayload(folder.Username, folder.Directory, folder.Files.Select(f => new SoulseekFile(f.Path, f.Size)).ToList());
            case Content.FilesRelease files:
                // The names become paths under the downloads folder: one plain segment each, whatever the package says.
                return new OpdsPayload(Id, KeyOf(contentId), Segment(files.Folder),
                    files.Files.Select(f => new OpdsFile(Segment(f.Name), f.Url.AbsoluteUri, f.Size, f.Unzip, f.Preference))
                        .DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList());
            default:
                throw Unknown();
        }
    }

    /// <summary>
    /// GET <paramref name="url"/> (a file of a release) into <paramref name="destination"/> with the source's rules and
    /// its <see cref="Content.ContentSource.FetchHeaders"/>: at most <paramref name="maxBytes"/>.
    /// </summary>
    public async Task<(bool Fits, long Bytes, string? ContentType)> FetchFileAsync(Uri url, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var (context, http) = NewCall(Timeout);
        try
        {
            return await http.DownloadAsync(url, _source.FetchHeaders(context), destination, maxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Api.SourceException ex)
        {
            throw Failure(ex, http);
        }
    }

    private async Task<Content.ContentRelease> ReleaseAsync(string contentId, CancellationToken cancellationToken)
    {
        var release = await CallAsync((context, ct) => _source.ResolveAsync(contentId, context, ct), cancellationToken, Timeout).ConfigureAwait(false);
        var expected = _source.Protocol switch
        {
            Content.ContentProtocol.Torrent => release is Content.TorrentRelease,
            Content.ContentProtocol.Soulseek => release is Content.SoulseekRelease { Files.Count: > 0 },
            _ => release is Content.FilesRelease { Files.Count: > 0 },
        };
        return expected ? release : throw Unknown();
    }

    private SourceException Unknown() =>
        new(SourceErrorKind.Error, $"{Name} returned no {_source.Protocol} release with files.");

    /// <summary>The torrent's metadata: from its .torrent, or fetched from the swarm by its magnet.</summary>
    private async Task<TorrentMetadata> TorrentAsync(Content.TorrentRelease torrent, CancellationToken cancellationToken)
    {
        var (bytes, magnet) = await TorrentLinkAsync(torrent, cancellationToken).ConfigureAwait(false);
        return bytes is not null
            ? Parse(bytes)
            : await Host.Torrents.FetchAsync(magnet!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The .torrent at its address, else its magnet link, else one made from its info hash.</summary>
    private async Task<(byte[]? Torrent, string? Magnet)> TorrentLinkAsync(Content.TorrentRelease torrent, CancellationToken cancellationToken)
    {
        SourceException? failure = null;
        if (torrent.TorrentUrl is { } url)
        {
            try
            {
                return await FetchTorrentAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceException ex) when (ex.Kind == SourceErrorKind.Error)
            {
                Logger.LogWarning("The .torrent of {Source} at {Url} failed: {Message}", Id, url.GetLeftPart(UriPartial.Path), ex.Message);
                failure = ex;
            }
        }

        if (!string.IsNullOrWhiteSpace(torrent.Magnet) && torrent.Magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return (null, torrent.Magnet.Trim());
        }

        if (MagnetUri.NormalizeInfoHash(torrent.InfoHash) is { } infoHash)
        {
            return (null, MagnetUri.Create(infoHash, null));
        }

        throw failure ?? new SourceException(SourceErrorKind.Error, "The release has no .torrent, magnet link or info hash.");
    }

    /// <summary>
    /// GET a .torrent with the source's proxy, cookies and <see cref="Content.ContentSource.FetchHeaders"/>: the address
    /// must be on the source's hosts; a redirect elsewhere (an indexer in Prowlarr's redirect mode sends the tracker's
    /// own link) is followed without the headers and cookies, which stay with the address's own host. A redirect to a
    /// <c>magnet:</c> link, or a body that is one, is that link.
    /// </summary>
    private async Task<(byte[]? Torrent, string? Magnet)> FetchTorrentAsync(Uri url, CancellationToken cancellationToken)
    {
        var (client, covers, cookies, timeout) = Direct();
        var (context, _) = NewCall(Timeout);
        var headers = _source.FetchHeaders(context) ?? new Dictionary<string, string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout ?? timeout);
        var current = url;
        if (!covers(url))
        {
            throw new SourceException(SourceErrorKind.Error, $"{Name} may not fetch {url.Scheme}://{url.Host}: it is not among its hosts.");
        }

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps)
            {
                throw new SourceException(SourceErrorKind.Error, $"The .torrent address redirects to an unsupported '{current.Scheme}:' address.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (Uri.Compare(current, url, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            {
                foreach (var (name, value) in headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                if (cookies.GetCookieHeader(current) is { Length: > 0 } cookie)
                {
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw SourceException.Unreachable(Name, ex);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SourceException(SourceErrorKind.Error, $"{Name} did not send the .torrent in time.");
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location
                                   ?? throw new SourceException(SourceErrorKind.Error, "The .torrent address redirects without a location.");
                    if (location.IsAbsoluteUri && location.Scheme == "magnet")
                    {
                        return (null, location.OriginalString);
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new SourceException(SourceErrorKind.AuthFailed, $"{Name} refused the .torrent ({(int)response.StatusCode}); check its settings.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new SourceException(SourceErrorKind.Error, $"The .torrent address answered {(int)response.StatusCode}.");
                }

                if (response.Content.Headers.ContentLength > MaxTorrentBytes)
                {
                    throw new SourceException(SourceErrorKind.Error, "The .torrent is too large.");
                }

                var bytes = await ReadAsync(response, deadline.Token).ConfigureAwait(false);
                return bytes.AsSpan().StartsWith("magnet:"u8)
                    ? (null, Encoding.UTF8.GetString(bytes).Trim())
                    : (bytes, null);
            }
        }

        throw new SourceException(SourceErrorKind.Error, "Too many redirects while fetching the .torrent.");
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxTorrentBytes)
            {
                throw new SourceException(SourceErrorKind.Error, "The .torrent is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private TorrentMetadata Parse(byte[] torrentFile)
    {
        try
        {
            return Host.Torrents.Parse(torrentFile);
        }
        catch (InvalidDataException ex)
        {
            throw new SourceException(SourceErrorKind.Error, $"{Name} returned an invalid .torrent file.", ex);
        }
    }

    /// <summary>A file-system safe key of a release: its content id hashed.</summary>
    /// <summary>
    /// A name a release gave as one file or folder name: separators and characters a file name cannot hold become '_',
    /// and nothing that names the folder itself or the one above (<c>.</c>, <c>..</c>, empty) is left.
    /// </summary>
    public static string Segment(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => c is '/' or '\\' || char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (clean.Length > 200)
        {
            clean = clean[..200].TrimEnd();
        }

        return clean.Trim('.').Length == 0 ? "_" + clean : clean;
    }

    private static string KeyOf(string contentId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contentId)))[..24];
}
