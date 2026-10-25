using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Captcha;

/// <summary>Where a check stands, as the service of checks says.</summary>
public enum ChallengeState
{
    /// <summary>Still being solved: by the service's browser, a person in a client's window, a notification.</summary>
    Pending,

    /// <summary>Passed: its cookies and the browser's User-Agent go with the requests to the domain.</summary>
    Solved,

    /// <summary>Nobody passed it in time, it was cancelled, or the site is not there.</summary>
    Failed,
}

/// <summary>A check of the service: its id is what a person opens to pass it.</summary>
/// <param name="Id">The check's id at the service.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Url">The page the check is on.</param>
/// <param name="ExpiresAt">When the service gives up on it.</param>
/// <param name="Message">Why it failed.</param>
public sealed record ChallengeOutcome(Guid Id, ChallengeState State, Uri Url, DateTimeOffset? ExpiresAt, string? Message);

/// <summary>A request made by the service's browser (<c>POST /fetch</c>): a domain whose cookies alone do not get past its check.</summary>
public sealed record BrowserRequest(Uri Url, string Method, IReadOnlyDictionary<string, string> Headers, byte[]? Body, TimeSpan Timeout);

/// <summary>What the service's browser got; <paramref name="Challenge"/> — the answer is itself a check.</summary>
public sealed record BrowserAnswer(int Status, Uri Url, IReadOnlyDictionary<string, string> Headers, byte[] Body, bool Challenge);

/// <summary>The service of checks failed (unreachable, a wrong key, an answer that is not its JSON).</summary>
public sealed class CaptchaServiceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Convy's link to the service of checks (captcha-solver): <c>POST /solve</c>, <c>GET /challenges/{id}</c> and
/// <c>POST /fetch</c> with Convy's key. What the service told about a domain — the cookies of a passed check, the
/// browser's User-Agent, whether the domain goes through the browser — is kept here for every plugin's requests, and so
/// are the checks still waiting for a person: the REST API names them to its client, and a later request takes the
/// cookies once a person passed one.
/// </summary>
public sealed class CaptchaService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>How much longer than its own wait a call to the service may take (the network, the service's queue).</summary>
    private static readonly TimeSpan ServiceSlack = TimeSpan.FromSeconds(15);

    /// <summary>A waiting check is asked about at most this often: the pages of a gallery go in parallel.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    /// <summary>The time a call of the REST API leaves for the waits of its checks (see <see cref="WaitWithin"/>): from when, how long.</summary>
    private static readonly AsyncLocal<(long Start, TimeSpan Budget)?> Deadline = new();

    private readonly IOptionsMonitor<CaptchaOptions> _options;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger<CaptchaService> _logger;
    private readonly ConcurrentDictionary<string, DomainState> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Waiting> _waiting = new(StringComparer.OrdinalIgnoreCase);

    public CaptchaService(IOptionsMonitor<CaptchaOptions> options, TimeProvider time, ILogger<CaptchaService> logger)
        : this(options, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, time, logger)
    {
    }

    /// <param name="handler">The transport to the service; tests pass a fake service.</param>
    public CaptchaService(IOptionsMonitor<CaptchaOptions> options, HttpMessageHandler handler, TimeProvider time, ILogger<CaptchaService> logger)
    {
        _options = options;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _time = time;
        _logger = logger;
    }

    /// <summary>The cookies of the passed checks (cf_clearance, __ddg*), for every plugin's requests.</summary>
    public CookieContainer Jar { get; } = new();

    /// <summary>Whether a service is set up (<c>captcha.url</c> and <c>captcha.key</c>).</summary>
    public bool IsSetUp => _options.CurrentValue.Upstream is not null;

    /// <summary>
    /// For <paramref name="budget"/> from now (and not longer) the checks of this async flow are waited for: a call of the
    /// REST API must answer its client in time, with the check that waits for a person if there is one. Dispose to end it.
    /// </summary>
    public static IDisposable WaitWithin(TimeSpan budget)
    {
        var previous = Deadline.Value;
        Deadline.Value = (Stopwatch.GetTimestamp(), budget);
        return new Restore(previous);
    }

    /// <summary>The User-Agent of the browser that passed the check of <paramref name="host"/>; null — none passed.</summary>
    public string? UserAgentFor(string host) => StateOf(host)?.UserAgent;

    /// <summary>Whether requests to <paramref name="host"/> go through the service's browser.</summary>
    public bool UsesBrowser(string host) => StateOf(host)?.Browser == true;

    /// <summary>The check of <paramref name="host"/> (or of its parent domain) that waits for a person; null — none.</summary>
    public ChallengeOutcome? WaitingFor(string host) => FindWaiting(host)?.Waiting.Check;

    /// <summary>
    /// Starts the check at <paramref name="url"/> (or joins the one going on) and waits for it as long as the options and
    /// the flow's <see cref="WaitWithin"/> allow. Passed — its cookies go into <see cref="Jar"/>, the domain's User-Agent
    /// and mode are remembered; not yet — it is remembered as waiting for a person. <paramref name="own"/> are the
    /// plugin's cookies for the site (a sign-in): they go with the check in Convy's own profile and stay the plugin's.
    /// </summary>
    /// <param name="afterTransplant">The site showed the check again to the cookies of a passed one: the service switches
    /// the domain to its browser.</param>
    /// <param name="site">A captcha of the site itself, which the plugin reported: only a person tells it is passed.</param>
    public async Task<ChallengeOutcome> SolveAsync(Uri url, IReadOnlyList<Cookie> own, bool afterTransplant, bool site, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var wait = TimeSpan.FromSeconds(Math.Clamp(options.WaitSeconds, 0, 60));
        if (Deadline.Value is { } call)
        {
            var left = call.Budget - Stopwatch.GetElapsedTime(call.Start);
            wait = left < wait ? left : wait;
        }

        var body = new SolveBody(url.AbsoluteUri)
        {
            Profile = ProfileOf(url, options),
            Cookies = own.Count > 0 ? own.Select(c => WireCookie.Of(c, url)).ToList() : null,
            Kind = site ? "site" : null,
            Wait = (int)Math.Clamp(Math.Floor(wait.TotalSeconds), 0, 60),
            AfterTransplant = afterTransplant,
        };
        var info = (await SendAsync<ChallengeBody>(HttpMethod.Post, "solve", body, TimeSpan.FromSeconds(body.Wait) + ServiceSlack, ct, missing: false)
            .ConfigureAwait(false))!;
        var check = Outcome(info, url);
        var domain = info.Domain ?? url.IdnHost;
        var sent = own.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        switch (check.State)
        {
            case ChallengeState.Solved:
                Remember(info, url, sent);
                _waiting.TryRemove(domain, out _);
                break;
            case ChallengeState.Pending:
                _waiting[domain] = new Waiting(check, _time.GetUtcNow(), sent);
                _logger.LogInformation("{Domain} asks to prove this is not a robot: check {Id} waits for a person until {ExpiresAt}.",
                    domain, check.Id, check.ExpiresAt);
                break;
            default:
                _waiting.TryRemove(domain, out _);
                break;
        }

        return check;
    }

    /// <summary>
    /// Asks about the check of <paramref name="host"/> that waited for a person (at most every few seconds): passed —
    /// its cookies are taken as from <see cref="SolveAsync"/>; failed or forgotten by the service — it no longer waits.
    /// A service that cannot be asked leaves it as it is.
    /// </summary>
    public async Task RefreshAsync(string host, CancellationToken ct)
    {
        if (FindWaiting(host) is not { } found || !IsSetUp)
        {
            return;
        }

        var (domain, waiting) = found;
        var now = _time.GetUtcNow();
        if (now - waiting.AskedAt < RefreshEvery)
        {
            return;
        }

        var check = waiting.Check;
        _waiting[domain] = waiting with { AskedAt = now };
        ChallengeBody? info;
        try
        {
            info = await SendAsync<ChallengeBody>(HttpMethod.Get, "challenges/" + check.Id, null, ServiceSlack, ct, missing: true).ConfigureAwait(false);
        }
        catch (CaptchaServiceException ex)
        {
            _logger.LogWarning("The check {Id} of {Domain} cannot be asked about: {Reason}", check.Id, domain, ex.Message);
            return;
        }

        var state = info is null ? ChallengeState.Failed : Outcome(info, check.Url).State;
        if (state == ChallengeState.Pending)
        {
            return;
        }

        _waiting.TryRemove(domain, out _);
        if (state == ChallengeState.Solved)
        {
            _logger.LogInformation("The check {Id} of {Domain} is passed.", check.Id, domain);
            Remember(info!, check.Url, waiting.Sent);
        }
    }

    /// <summary>The request made by the service's browser, with the plugin's own cookies of <paramref name="own"/>.</summary>
    public async Task<BrowserAnswer> FetchAsync(BrowserRequest request, IReadOnlyList<Cookie> own, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var answer = (await SendAsync<FetchAnswer>(HttpMethod.Post, "fetch", new FetchBody(request.Url.AbsoluteUri)
        {
            Method = request.Method,
            Headers = request.Headers,
            Body = request.Body is { Length: > 0 } bytes ? Convert.ToBase64String(bytes) : null,
            Profile = ProfileOf(request.Url, options),
            Cookies = own.Count > 0 ? own.Select(c => WireCookie.Of(c, request.Url)).ToList() : null,
            Timeout = (int)Math.Ceiling(request.Timeout.TotalSeconds),
        }, request.Timeout + ServiceSlack, ct, missing: false).ConfigureAwait(false))!;
        try
        {
            return new BrowserAnswer(answer.Status, answer.Url ?? request.Url, answer.Headers ?? new Dictionary<string, string>(),
                string.IsNullOrEmpty(answer.Body) ? [] : Convert.FromBase64String(answer.Body), answer.Challenge is not null);
        }
        catch (FormatException ex)
        {
            throw new CaptchaServiceException("The service of checks answered a body that is not base64.", ex);
        }
    }

    /// <summary>Convy's own profile for the host: the service keeps a plugin's cookies only there.</summary>
    private static string ProfileOf(Uri url, CaptchaOptions options) =>
        $"{url.IdnHost.ToLowerInvariant()}#{(string.IsNullOrWhiteSpace(options.Profile) ? "convy" : options.Profile.Trim())}";

    private static ChallengeOutcome Outcome(ChallengeBody info, Uri url) =>
        new(info.Id, info.State switch
        {
            "solved" => ChallengeState.Solved,
            "failed" => ChallengeState.Failed,
            _ => ChallengeState.Pending,
        }, info.Url ?? url, info.ExpiresAt, info.Message);

    /// <summary>
    /// The cookies of a solved check go into the jar as the site would have set them; the domain's User-Agent and mode
    /// are remembered. The service answers with every cookie of the profile: those the plugin sent (<paramref name="sent"/>)
    /// stay the plugin's.
    /// </summary>
    private void Remember(ChallengeBody info, Uri url, HashSet<string> sent)
    {
        var state = _domains.GetOrAdd(info.Domain ?? url.IdnHost, _ => new DomainState());
        if (info.UserAgent is { Length: > 0 } agent)
        {
            state.UserAgent = agent;
        }

        state.Browser = info.Mode == "browser";
        foreach (var c in info.Cookies ?? [])
        {
            if (sent.Contains(c.Name) || string.IsNullOrEmpty(c.Name))
            {
                continue;
            }

            state.Given[c.Name] = 0;
            try
            {
                var cookie = new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain)
                {
                    HttpOnly = c.HttpOnly,
                    Secure = c.Secure,
                };
                if (c.Expires is { } expires)
                {
                    cookie.Expires = expires.UtcDateTime;
                }

                Jar.Add(cookie);
            }
            catch (CookieException)
            {
                // A cookie the jar refuses (a domain it cannot hold) is left out, as from a site.
            }
        }
    }

    private (string Domain, Waiting Waiting)? FindWaiting(string host)
    {
        for (var domain = host; domain.Length > 0;)
        {
            if (_waiting.TryGetValue(domain, out var waiting))
            {
                return (domain, waiting);
            }

            var dot = domain.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                break;
            }

            domain = domain[(dot + 1)..];
        }

        return null;
    }

    private DomainState? StateOf(string host)
    {
        // The state of the host or of the nearest parent domain the check was for.
        for (var domain = host; domain.Length > 0;)
        {
            if (_domains.TryGetValue(domain, out var state))
            {
                return state;
            }

            var dot = domain.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                break;
            }

            domain = domain[(dot + 1)..];
        }

        return null;
    }

    /// <summary>
    /// The service's JSON answer within <paramref name="timeout"/>; <paramref name="missing"/> — a 404 is null rather
    /// than a failure. Whatever goes wrong on the way is <see cref="CaptchaServiceException"/>.
    /// </summary>
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken ct, bool missing)
        where T : class
    {
        var (service, key) = _options.CurrentValue.Upstream ?? throw new CaptchaServiceException("No service of checks is set up (captcha.url, captcha.key).");
        using var request = new HttpRequestMessage(method, new Uri(service, path))
        {
            Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (missing && response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
                string? error = null;
                try
                {
                    error = JsonDocument.Parse(text).RootElement.GetProperty("error").GetString();
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                }

                throw new CaptchaServiceException(string.Create(CultureInfo.InvariantCulture,
                    $"The service of checks answered {(int)response.StatusCode}{(error is null ? "" : ": " + error)}."));
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, limit.Token).ConfigureAwait(false)
                   ?? throw new CaptchaServiceException("The service of checks answered nothing.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CaptchaServiceException(string.Create(CultureInfo.InvariantCulture,
                $"The service of checks did not answer in {timeout.TotalSeconds:0} s."));
        }
        catch (HttpRequestException ex)
        {
            throw new CaptchaServiceException($"The service of checks cannot be reached: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new CaptchaServiceException($"The service of checks answered something that is not its JSON: {ex.Message}", ex);
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class Restore((long Start, TimeSpan Budget)? previous) : IDisposable
    {
        public void Dispose() => Deadline.Value = previous;
    }

    /// <summary>A check waiting for a person, when the service was last asked about it, and the names of the plugin's cookies that went with it.</summary>
    private sealed record Waiting(ChallengeOutcome Check, DateTimeOffset AskedAt, HashSet<string> Sent);

    /// <summary>What the service told about a domain the last time one of its checks was solved.</summary>
    private sealed class DomainState
    {
        public string? UserAgent { get; set; }

        public bool Browser { get; set; }

        /// <summary>The cookies of the checks (cf_clearance, __ddg*): not the site's own, so never sent back as them.</summary>
        public ConcurrentDictionary<string, byte> Given { get; } = new(StringComparer.Ordinal);
    }

    private sealed record WireCookie(string Name, string Value, string Domain, string Path = "/")
    {
        public DateTimeOffset? Expires { get; init; }

        public bool HttpOnly { get; init; }

        public bool Secure { get; init; }

        public static WireCookie Of(Cookie cookie, Uri url) =>
            new(cookie.Name, cookie.Value, cookie.Domain.TrimStart('.') is { Length: > 0 } domain ? domain : url.IdnHost,
                string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path);
    }

    private sealed record SolveBody(string Url)
    {
        public string? Profile { get; init; }

        public List<WireCookie>? Cookies { get; init; }

        public string? Kind { get; init; }

        public int Wait { get; init; }

        public bool AfterTransplant { get; init; }
    }

    private sealed record ChallengeBody
    {
        public required Guid Id { get; init; }

        public string? State { get; init; }

        public string? Domain { get; init; }

        public Uri? Url { get; init; }

        public DateTimeOffset? ExpiresAt { get; init; }

        public string? Message { get; init; }

        public List<WireCookie>? Cookies { get; init; }

        public string? UserAgent { get; init; }

        public string? Mode { get; init; }
    }

    private sealed record FetchBody(string Url)
    {
        public string Method { get; init; } = "GET";

        public IReadOnlyDictionary<string, string>? Headers { get; init; }

        public string? Profile { get; init; }

        public string? Body { get; init; }

        public List<WireCookie>? Cookies { get; init; }

        public int Timeout { get; init; }
    }

    private sealed record FetchAnswer
    {
        public int Status { get; init; }

        public Uri? Url { get; init; }

        public Dictionary<string, string>? Headers { get; init; }

        public string? Body { get; init; }

        public string? Challenge { get; init; }
    }
}
