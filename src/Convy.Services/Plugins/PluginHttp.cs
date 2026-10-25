using System.Net;
using Convy.Services.Captcha;
using Convy.Sources;

namespace Convy.Services.Plugins;

/// <summary>
/// HTTP for one plugin: requests may go only to the hosts of its manifest, through the proxy of its settings, at most
/// <see cref="PluginManifest.RequestsPerSecond"/> of them per second, each within the configured timeout. Transport
/// failures are <see cref="SourceException"/>s; a status is returned as is, except a bot check (Cloudflare,
/// DDoS-Guard): that is handed to the service of checks (<see cref="CaptchaService"/>) and, passed, the request goes
/// again with the browser's cookies and User-Agent, or through the service's browser where those are not enough. A
/// check not passed in time, or with no service set up, is a <see cref="SourceErrorKind.Challenge"/>.
/// </summary>
public sealed class PluginHttp : IPluginHttp, IDisposable
{
    private readonly PluginManifest _manifest;
    private readonly HttpClient _client;
    private readonly Func<TimeSpan> _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly CaptchaService? _captcha;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    /// <param name="handler">The transport (a <see cref="SocketsHttpHandler"/> with the proxy, or a test handler).</param>
    /// <param name="timeout">How long one request may take; read on every request.</param>
    /// <param name="captcha">The service of checks; without it a check is only reported.</param>
    public PluginHttp(PluginManifest manifest, HttpMessageHandler handler, Func<TimeSpan> timeout, TimeProvider timeProvider, CaptchaService? captcha = null)
    {
        _manifest = manifest;
        _captcha = captcha;
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

    /// <summary>The largest error answer read to tell a bot check from the site's own error page.</summary>
    private const int MaxCheckBytes = 1024 * 1024;

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

        if (_captcha is not { IsSetUp: true } captcha)
        {
            var plain = await SendDirectAsync(request, uri, streaming, null, cancellationToken).ConfigureAwait(false);
            if (await IsChallengeAsync(plain, cancellationToken).ConfigureAwait(false))
            {
                plain.Dispose();
                throw Challenge(Final(plain, uri), "no service of checks is set up (captcha.url, captcha.key).");
            }

            return plain;
        }

        // A person may have passed the check of this site meanwhile: its cookies go with this request already.
        await captcha.RefreshAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
        // The request may go again: its body is read once.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (captcha.UsesBrowser(uri.IdnHost))
        {
            return await ThroughBrowserAsync(captcha, request, uri, body, cancellationToken).ConfigureAwait(false);
        }

        var response = await SendDirectAsync(Copy(request, uri, body), uri, streaming, captcha, cancellationToken).ConfigureAwait(false);
        if (!await IsChallengeAsync(response, cancellationToken).ConfigureAwait(false))
        {
            return response;
        }

        var at = Final(response, uri);
        response.Dispose();
        await PassAsync(captcha, request, uri, at, afterTransplant: false, cancellationToken).ConfigureAwait(false);
        if (!captcha.UsesBrowser(at.IdnHost))
        {
            response = await SendDirectAsync(Copy(request, uri, body), uri, streaming, captcha, cancellationToken).ConfigureAwait(false);
            if (!await IsChallengeAsync(response, cancellationToken).ConfigureAwait(false))
            {
                return response;
            }

            // Passed, and the site still shows the check to the browser's cookies: it looks at more (TLS, HTTP/2), so the
            // service's browser makes the requests to it from now on.
            at = Final(response, uri);
            response.Dispose();
            await PassAsync(captcha, request, uri, at, afterTransplant: true, cancellationToken).ConfigureAwait(false);
        }

        return await ThroughBrowserAsync(captcha, request, uri, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The request as the plugin built it, the way it goes to the site; redirects followed and checked hop by hop.</summary>
    private async Task<HttpResponseMessage> SendDirectAsync(HttpRequestMessage request, Uri uri, bool streaming, CaptchaService? captcha, CancellationToken cancellationToken)
    {
        await WaitForTurnAsync(cancellationToken).ConfigureAwait(false);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout());
        var own = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null;
        try
        {
            var completion = streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
            Dress(request, uri, own, captcha);
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
                Dress(hop, next, string.Equals(next.Host, uri.Host, StringComparison.OrdinalIgnoreCase) ? own : null, captcha);
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

    /// <summary>
    /// The cookies of the passed checks next to the plugin's own (<paramref name="own"/>, for the first host only), and the
    /// User-Agent of the browser that passed them: a check's cookie holds only with it.
    /// </summary>
    private static void Dress(HttpRequestMessage message, Uri url, string? own, CaptchaService? captcha)
    {
        if (captcha is null)
        {
            return;
        }

        message.Headers.Remove("Cookie");
        // The plugin's own cookie wins over a check's cookie of the same name.
        var mine = (own ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)[0])
            .ToHashSet(StringComparer.Ordinal);
        var checks = string.Join("; ", captcha.Jar.GetCookies(url).Where(c => !mine.Contains(c.Name)).Select(c => $"{c.Name}={c.Value}"));
        var cookie = string.Join("; ", new[] { checks, own }.Where(c => !string.IsNullOrEmpty(c)));
        if (cookie.Length > 0)
        {
            message.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (captcha.UserAgentFor(url.IdnHost) is { } agent)
        {
            message.Headers.Remove("User-Agent");
            message.Headers.TryAddWithoutValidation("User-Agent", agent);
        }
    }

    /// <summary>The plugin's request once more: the same method, headers and body.</summary>
    private static HttpRequestMessage Copy(HttpRequestMessage request, Uri uri, byte[]? body)
    {
        var copy = new HttpRequestMessage(request.Method, uri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers)
            {
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return copy;
    }

    /// <summary>
    /// Asks the service to pass the check at <paramref name="at"/> (where <paramref name="uri"/> led): returns once it is
    /// passed; still waiting for a person, failed, or the service failing is the request's <see cref="SourceErrorKind.Challenge"/>.
    /// </summary>
    private async Task PassAsync(CaptchaService captcha, HttpRequestMessage request, Uri uri, Uri at, bool afterTransplant, CancellationToken cancellationToken)
    {
        ChallengeOutcome check;
        try
        {
            check = await captcha.SolveAsync(at, Place(request, uri, at), afterTransplant, site: false, cancellationToken).ConfigureAwait(false);
        }
        catch (CaptchaServiceException ex)
        {
            throw Challenge(at, ex.Message);
        }

        switch (check.State)
        {
            case ChallengeState.Solved:
                return;
            case ChallengeState.Pending:
                throw Challenge(at, $"a person has to pass it (check {check.Id}).");
            default:
                throw Challenge(at, $"the service of checks could not pass it{(check.Message is { } why ? ": " + why : ".")}");
        }
    }

    /// <summary>The request made by the service's browser; a check in its answer is passed once more and the request made again.</summary>
    private async Task<HttpResponseMessage> ThroughBrowserAsync(CaptchaService captcha, HttpRequestMessage request, Uri uri, byte[]? body, CancellationToken cancellationToken)
    {
        var answer = await BrowserAsync(captcha, request, uri, body, cancellationToken).ConfigureAwait(false);
        if (!answer.Challenge)
        {
            return answer.Response;
        }

        var at = Final(answer.Response, uri);
        answer.Response.Dispose();
        await PassAsync(captcha, request, uri, at, afterTransplant: false, cancellationToken).ConfigureAwait(false);
        answer = await BrowserAsync(captcha, request, uri, body, cancellationToken).ConfigureAwait(false);
        if (!answer.Challenge)
        {
            return answer.Response;
        }

        answer.Response.Dispose();
        throw Challenge(at, "the site still shows it to the service's browser.");
    }

    private async Task<(HttpResponseMessage Response, bool Challenge)> BrowserAsync(
        CaptchaService captcha, HttpRequestMessage request, Uri uri, byte[]? body, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            // The browser sends its own Host and User-Agent; the plugin's cookies go to the service as cookies.
            if (name is not ("Host" or "User-Agent" or "Cookie"))
            {
                headers[name] = string.Join(", ", values);
            }
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name] = string.Join(", ", values);
            }
        }

        await WaitForTurnAsync(cancellationToken).ConfigureAwait(false);
        BrowserAnswer answer;
        try
        {
            answer = await captcha.FetchAsync(new BrowserRequest(uri, request.Method.Method, headers, body, _timeout()), Place(request, uri, uri), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CaptchaServiceException ex)
        {
            throw new SourceException(SourceErrorKind.Error, $"{uri.Host} goes through the browser of the service of checks, which failed: {ex.Message}", ex);
        }

        // The browser followed the redirects itself: where it ended must still be the plugin's.
        if (answer.Url.Scheme is not ("http" or "https") || !Allows(answer.Url.Host))
        {
            throw new SourceException(SourceErrorKind.Error, $"{uri.Host} redirected to {answer.Url.Host}, which is not among the plugin's domains.");
        }

        var response = new HttpResponseMessage((HttpStatusCode)answer.Status)
        {
            Content = new ByteArrayContent(answer.Body),
            RequestMessage = new HttpRequestMessage(request.Method, answer.Url),
        };
        foreach (var (name, value) in answer.Headers)
        {
            // The browser decoded the body already.
            if (string.Equals(name, "Content-Encoding", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return (response, answer.Challenge);
    }

    /// <summary>
    /// Where at the service a check at <paramref name="at"/> works: the manifest's domain that covers it, the plugin's own
    /// profile, and the plugin's cookies — only when <paramref name="at"/> is the host the plugin sent them to.
    /// </summary>
    private ChallengePlace Place(HttpRequestMessage request, Uri uri, Uri at) =>
        new(CoveringDomain(_manifest.Domains, at.IdnHost), _manifest.Id,
            string.Equals(at.Host, uri.Host, StringComparison.OrdinalIgnoreCase) ? OwnCookies(request, at) : []);

    /// <summary>The domain of <paramref name="domains"/> that covers <paramref name="host"/> (the longest); the host itself when none does.</summary>
    public static string CoveringDomain(IReadOnlyList<string> domains, string host) =>
        domains.Where(d => string.Equals(host, d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Length)
            .FirstOrDefault()?.ToLowerInvariant() ?? host.ToLowerInvariant();

    /// <summary>The plugin's own cookies of its request (a sign-in), for its profile of the site.</summary>
    private static List<Cookie> OwnCookies(HttpRequestMessage request, Uri url)
    {
        var cookies = new List<Cookie>();
        if (!request.Headers.TryGetValues("Cookie", out var values))
        {
            return cookies;
        }

        foreach (var pair in values.SelectMany(v => v.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            cookies.RemoveAll(c => c.Name == pair[..eq]);
            try
            {
                cookies.Add(new Cookie(pair[..eq], pair[(eq + 1)..], "/", url.IdnHost));
            }
            catch (CookieException)
            {
                // A pair no cookie can hold is not sent by a browser either.
            }
        }

        return cookies;
    }

    /// <summary>
    /// Whether the answer is a bot check. Only an error answer whose headers may be one is read for it, at most
    /// <see cref="MaxCheckBytes"/>; it stays readable for the plugin.
    /// </summary>
    private static async Task<bool> IsChallengeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var header = ChallengeDetector.Header(response);
        if (response.IsSuccessStatusCode || !ChallengeDetector.MayBe((int)response.StatusCode, header))
        {
            return false;
        }

        if (response.Content.Headers.ContentLength > MaxCheckBytes)
        {
            // Larger than any check page: the site's own answer, left unread for the plugin.
            return ChallengeDetector.IsChallenge((int)response.StatusCode, header, null);
        }

        try
        {
            await response.Content.LoadIntoBufferAsync(MaxCheckBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Larger than any check page (or broken off): the site's own answer.
            return ChallengeDetector.IsChallenge((int)response.StatusCode, header, null);
        }

        return ChallengeDetector.IsChallenge(response, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Where an answer came from (after the redirects).</summary>
    private static Uri Final(HttpResponseMessage response, Uri uri) => response.RequestMessage?.RequestUri ?? uri;

    /// <summary>Marks the checks this HTTP saw itself (and handed to the service), as opposed to a site's captcha a plugin reports.</summary>
    private const string CheckedMark = "Convy.Captcha.Checked";

    /// <summary>Whether <paramref name="exception"/> is a bot check this HTTP saw and handed to the service already.</summary>
    public static bool Checked(SourceException exception) => exception.Data.Contains(CheckedMark);

    private static SourceException Challenge(Uri at, string why)
    {
        var exception = new SourceException(SourceErrorKind.Challenge, $"{at.Host} asks to prove this is not a robot ({at.AbsoluteUri}); {why}")
        {
            ChallengeUrl = at,
        };
        exception.Data[CheckedMark] = true;
        return exception;
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
