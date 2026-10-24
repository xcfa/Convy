using System.Net;
using Convy.Sources;

namespace Convy.Services.Plugins;

/// <summary>
/// HTTP for one plugin: requests may go only to the hosts of its manifest, through the proxy of its settings, at most
/// <see cref="PluginManifest.RequestsPerSecond"/> of them per second, each within the configured timeout. Transport
/// failures are <see cref="SourceException"/>s; a status is returned as is.
/// </summary>
public sealed class PluginHttp : IPluginHttp, IDisposable
{
    private readonly PluginManifest _manifest;
    private readonly HttpClient _client;
    private readonly Func<TimeSpan> _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    /// <param name="handler">The transport (a <see cref="SocketsHttpHandler"/> with the proxy, or a test handler).</param>
    /// <param name="timeout">How long one request may take; read on every request.</param>
    public PluginHttp(PluginManifest manifest, HttpMessageHandler handler, Func<TimeSpan> timeout, TimeProvider timeProvider)
    {
        _manifest = manifest;
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Convy/1.0");
        _timeout = timeout;
        _timeProvider = timeProvider;
        _interval = manifest.RequestsPerSecond is { } rate and > 0 ? TimeSpan.FromSeconds(1 / rate) : TimeSpan.Zero;
    }

    /// <summary>At most this many redirects are followed, each hop checked against the manifest's domains.</summary>
    public const int MaxRedirects = 5;

    /// <summary>The transport of a plugin's settings: the proxy of <paramref name="proxy"/>, no automatic redirects (<see cref="SendAsync"/> follows them hop by hop), no cookie jar (plugins send their cookies themselves).</summary>
    public static SocketsHttpHandler CreateHandler(string? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(new Uri(proxy.Trim()));
            handler.UseProxy = true;
        }

        return handler;
    }

    /// <summary>Whether <paramref name="host"/> is one of the manifest's domains or a subdomain of one.</summary>
    public bool Allows(string host) =>
        _manifest.Domains.Any(d => string.Equals(host, d, StringComparison.OrdinalIgnoreCase)
                                   || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool streaming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is not { IsAbsoluteUri: true } uri || uri.Scheme is not ("http" or "https"))
        {
            throw new SourceException(SourceErrorKind.Error, "The request needs an absolute http(s) address.");
        }

        if (!Allows(uri.Host))
        {
            throw new SourceException(SourceErrorKind.Error, $"{uri.Host} is not among the plugin's domains ({string.Join(", ", _manifest.Domains)}).");
        }

        await WaitForTurnAsync(cancellationToken).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout());
        try
        {
            var completion = streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
            var response = await _client.SendAsync(request, completion, deadline.Token).ConfigureAwait(false);
            // Redirects are followed here, not by the handler, so that every hop is checked: a chain through a host
            // outside the manifest, or down from https to http, is refused before anything is sent there.
            for (var hops = 0; IsRedirect(response.StatusCode) && response.Headers.Location is { } location; hops++)
            {
                var from = response.RequestMessage?.RequestUri ?? uri;
                var next = location.IsAbsoluteUri ? location : new Uri(from, location);
                response.Dispose();
                if (hops == MaxRedirects)
                {
                    throw new SourceException(SourceErrorKind.Error, $"{uri.Host} redirected more than {MaxRedirects} times.");
                }

                if (next.Scheme is not ("http" or "https") || !Allows(next.Host))
                {
                    throw new SourceException(SourceErrorKind.Error, $"{uri.Host} redirected to {next.Host}, which is not among the plugin's domains.");
                }

                if (from.Scheme == "https" && next.Scheme == "http")
                {
                    throw new SourceException(SourceErrorKind.Error, $"{uri.Host} redirected from https to http ({next.Host}); refused.");
                }

                using var hop = Follow(request, from, next, response.StatusCode);
                response = await _client.SendAsync(hop, completion, deadline.Token).ConfigureAwait(false);
            }

            return response;
        }
        catch (HttpRequestException ex)
        {
            throw SourceException.Unreachable(uri.Host, ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceException(SourceErrorKind.Error, $"{uri.Host} did not answer within {_timeout().TotalSeconds:0} s.");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// The request of the next hop: GET for 303 (and for 301/302 after a POST, as browsers do), the same method and body
    /// otherwise; the original headers, minus credentials when the host changes.
    /// </summary>
    private static HttpRequestMessage Follow(HttpRequestMessage original, Uri from, Uri next, HttpStatusCode status)
    {
        var toGet = status == HttpStatusCode.SeeOther
                    || (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && original.Method == HttpMethod.Post);
        var hop = new HttpRequestMessage(toGet ? HttpMethod.Get : original.Method, next);
        if (!toGet && original.Content is not null)
        {
            // The body of the first request was consumed by sending it; a site that redirects a POST with 307/308 is rare
            // enough not to buffer every body for.
            throw new SourceException(SourceErrorKind.Error, $"{from.Host} redirected a {original.Method} request with a body ({(int)status}); not followed.");
        }

        var sameHost = string.Equals(from.Host, next.Host, StringComparison.OrdinalIgnoreCase);
        foreach (var header in original.Headers)
        {
            if (!sameHost && header.Key is "Authorization" or "Cookie" or "Proxy-Authorization")
            {
                continue;
            }

            hop.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return hop;
    }

    private async Task WaitForTurnAsync(CancellationToken cancellationToken)
    {
        if (_interval == TimeSpan.Zero)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _lastRequestAt + _interval - _timeProvider.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestAt = _timeProvider.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }
}
