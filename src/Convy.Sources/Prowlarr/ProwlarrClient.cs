using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Convy.Sources.Prowlarr;

/// <summary>Connection to Prowlarr (<c>PROWLARR__URL</c>, <c>PROWLARR__APIKEY</c>).</summary>
public sealed class ProwlarrOptions
{
    public const string SectionName = "Prowlarr";

    public string? Url { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Whether Prowlarr is configured; without it the provider offers no sources.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>A Prowlarr indexer (<c>GET /api/v1/indexer</c>).</summary>
public sealed record ProwlarrIndexer(int Id, string? Name, bool Enable, string? Protocol, int Priority);

/// <summary>A currently blocked indexer (<c>GET /api/v1/indexerstatus</c> lists only those).</summary>
public sealed record ProwlarrIndexerStatus(int IndexerId, DateTimeOffset? DisabledTill, DateTimeOffset? MostRecentFailure);

/// <summary>A release returned by <c>GET /api/v1/search</c>.</summary>
public sealed record ProwlarrRelease
{
    public string? Guid { get; init; }
    public string? Title { get; init; }
    public long Size { get; init; }
    public int? Files { get; init; }
    public int IndexerId { get; init; }
    public string? DownloadUrl { get; init; }
    public string? MagnetUrl { get; init; }
    public string? InfoHash { get; init; }
    public int? Seeders { get; init; }
    public int? Leechers { get; init; }
    public string? Protocol { get; init; }
}

/// <summary>What a Prowlarr download link resolved to: .torrent bytes or a magnet URI.</summary>
public sealed record ProwlarrDownload(byte[]? TorrentFile, string? Magnet);

/// <summary>
/// HTTP client for the Prowlarr v1 API. The API key goes into the <c>X-Api-Key</c> header of
/// API calls only; download links already carry it and redirects to third-party hosts never
/// receive it. The <see cref="HttpClient"/> must not follow redirects automatically.
/// </summary>
public sealed class ProwlarrClient
{
    private const int MaxRedirects = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ProwlarrOptions _options;

    public ProwlarrClient(HttpClient http, ProwlarrOptions options)
    {
        _http = http;
        _options = options;
    }

    public Task<IReadOnlyList<ProwlarrIndexer>> GetIndexersAsync(CancellationToken cancellationToken) =>
        GetApiAsync<ProwlarrIndexer>("api/v1/indexer", cancellationToken);

    public Task<IReadOnlyList<ProwlarrIndexerStatus>> GetIndexerStatusesAsync(CancellationToken cancellationToken) =>
        GetApiAsync<ProwlarrIndexerStatus>("api/v1/indexerstatus", cancellationToken);

    /// <summary>Searches one indexer for one query (one request per indexer and variant).</summary>
    public Task<IReadOnlyList<ProwlarrRelease>> SearchAsync(
        string query, int indexerId, IReadOnlyList<int> categories, CancellationToken cancellationToken)
    {
        // Several values are passed by repeating the key; Prowlarr does not split commas.
        var path = $"api/v1/search?query={Uri.EscapeDataString(query)}&indexerIds={indexerId}&type=search&limit=100"
                   + string.Concat(categories.Select(c => $"&categories={c}"));
        return GetApiAsync<ProwlarrRelease>(path, cancellationToken);
    }

    /// <summary>
    /// Fetches a Prowlarr download link (<c>downloadUrl</c>/<c>magnetUrl</c> of a release).
    /// Prowlarr answers with the .torrent bytes or redirects to a magnet URI (or, for
    /// indexers in redirect mode, to the tracker's own link, which is followed).
    /// </summary>
    public async Task<ProwlarrDownload> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        var current = new Uri(_http.BaseAddress!, url);

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, current), cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                               ?? throw new SourceException(SourceErrorKind.Error, "Prowlarr redirected without a location.");

                if (location.IsAbsoluteUri && location.Scheme == "magnet")
                {
                    return new ProwlarrDownload(null, location.OriginalString);
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps)
                {
                    throw new SourceException(SourceErrorKind.Error, $"The download link redirects to an unsupported '{current.Scheme}:' URI.");
                }

                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw AuthFailed();
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
                throw new SourceException(SourceErrorKind.Error, $"Download link failed with {(int)response.StatusCode}{detail}.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.AsSpan().StartsWith("magnet:"u8))
            {
                return new ProwlarrDownload(null, System.Text.Encoding.UTF8.GetString(bytes).Trim());
            }

            return new ProwlarrDownload(bytes, null);
        }

        throw new SourceException(SourceErrorKind.Error, "Too many redirects while fetching the download link.");
    }

    private async Task<IReadOnlyList<T>> GetApiAsync<T>(string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", _options.ApiKey);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw AuthFailed();
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            throw new SourceException(SourceErrorKind.Error, $"Prowlarr returned {(int)response.StatusCode}{detail}.");
        }

        return await response.Content.ReadFromJsonAsync<List<T>>(Json, cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            try
            {
                return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new SourceException(SourceErrorKind.Error, $"Prowlarr is unreachable: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Extracts Prowlarr's error text (JSON <c>message</c> or XML <c>description</c>).</summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message))
            {
                return $": {message.GetString()}";
            }
        }
        catch (JsonException)
        {
            var start = body.IndexOf("description=\"", StringComparison.Ordinal);
            if (start >= 0)
            {
                start += "description=\"".Length;
                var end = body.IndexOf('"', start);
                if (end > start)
                {
                    return $": {WebUtility.HtmlDecode(body[start..end])}";
                }
            }
        }

        return string.Empty;
    }

    private static bool IsRedirect(HttpStatusCode code) => (int)code is >= 300 and < 400;

    private static SourceException AuthFailed() =>
        new(SourceErrorKind.AuthFailed, "Prowlarr rejected the API key.");
}
