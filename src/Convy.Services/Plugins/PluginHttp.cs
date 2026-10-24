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

    /// <summary>The transport of a plugin's settings: the proxy of <paramref name="proxy"/>, redirects followed, no cookie jar (plugins send their cookies themselves).</summary>
    public static SocketsHttpHandler CreateHandler(string? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
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
            var response = await _client
                .SendAsync(request, streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && !Allows(final.Host))
            {
                response.Dispose();
                throw new SourceException(SourceErrorKind.Error, $"{uri.Host} redirected to {final.Host}, which is not among the plugin's domains.");
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
