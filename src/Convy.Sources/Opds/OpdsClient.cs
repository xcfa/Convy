using System.Net;

namespace Convy.Sources.Opds;

/// <summary>An OPDS catalog Convy searches and downloads from.</summary>
public sealed record OpdsCatalog
{
    /// <summary>Short id, part of the source id <c>opds:&lt;id&gt;</c>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The catalog's root feed, e.g. <c>https://flibusta.site/opds</c>.</summary>
    public required Uri Url { get; init; }

    /// <summary>
    /// Book search address with <c>{searchTerms}</c>, relative to <see cref="Url"/> or absolute;
    /// <c>null</c> to use the search link the root feed announces.
    /// </summary>
    public string? SearchUrl { get; init; }

    /// <summary>Author search address with <c>{searchTerms}</c>; <c>null</c> when the catalog has none.</summary>
    public string? AuthorSearchUrl { get; init; }

    /// <summary>
    /// Flibusta-style author feeds: <c>&lt;author&gt;/alphabet</c>, then <c>/alphabet/1</c>, <c>/2</c>, …
    /// with 20 books each, so several pages can be read at once.
    /// </summary>
    public bool FlibustaAuthorPages { get; init; }

    /// <summary>Formats in order of preference (<c>epub</c>, <c>fb2</c>, …); the first one available is downloaded by default.</summary>
    public IReadOnlyList<string> Formats { get; init; } = ["epub", "fb2"];

    /// <summary>Store the <c>.fb2</c> inside a <c>.fb2.zip</c> rather than the archive.</summary>
    public bool UnzipFb2 { get; init; } = true;

    /// <summary>How long one search or listing may take; the catalog may be slow.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Pages of an author's books read for one author found by a search.</summary>
    public int AuthorPages { get; init; } = 3;

    /// <summary>Authors whose books are listed for one query (those with the most books).</summary>
    public int MaxAuthors { get; init; } = 2;

    /// <summary>Requests sent to the catalog at the same time.</summary>
    public int MaxConcurrentRequests { get; init; } = 4;
}

/// <summary>
/// HTTP access to one OPDS catalog: feeds for searching and files for downloading. Limits the
/// requests sent at the same time, since catalogs such as Flibusta are slow and easily overloaded.
/// </summary>
public sealed class OpdsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate;

    /// <param name="catalog">The catalog.</param>
    /// <param name="http">A client configured for the catalog (proxy, credentials); owned by this instance.</param>
    public OpdsClient(OpdsCatalog catalog, HttpClient http)
    {
        Catalog = catalog;
        _http = http;
        _gate = new SemaphoreSlim(Math.Max(1, catalog.MaxConcurrentRequests));
    }

    public OpdsCatalog Catalog { get; }

    /// <summary>Reads and parses a feed. Throws <see cref="SourceException"/> on HTTP or format errors.</summary>
    public async Task<OpdsFeed> GetFeedAsync(Uri url, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Parse from memory: the parser is synchronous and must not block on the network.
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            return OpdsFeedParser.Parse(buffer, response.RequestMessage?.RequestUri ?? url);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts downloading a file; the caller reads and disposes the response. Throws
    /// <see cref="SourceException"/> when the catalog answers with an error.
    /// </summary>
    public async Task<HttpResponseMessage> GetFileAsync(Uri url, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendAsync(url, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken cancellationToken)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            throw new SourceException(SourceErrorKind.Error, $"Refusing to fetch '{url}': only http and https are allowed.");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException(SourceErrorKind.Error, $"{Catalog.Name} is unreachable: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();
        throw new SourceException(
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? SourceErrorKind.AuthFailed : SourceErrorKind.Error,
            $"{Catalog.Name} answered {(int)status} {status} for {url.PathAndQuery}.");
    }
}

/// <summary>The configured catalogs with their clients.</summary>
public sealed class OpdsCatalogs : IDisposable
{
    private readonly IReadOnlyList<OpdsClient> _clients;

    public OpdsCatalogs(IReadOnlyList<OpdsClient> clients) => _clients = clients;

    public IReadOnlyList<OpdsClient> All => _clients;

    /// <summary>The catalog with the given id, or <c>null</c>.</summary>
    public OpdsClient? Find(string id) => _clients.FirstOrDefault(c => string.Equals(c.Catalog.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }
}
