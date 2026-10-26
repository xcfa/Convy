using System.Runtime.ExceptionServices;
using System.Text;
using Convy.Sources;
using Microsoft.Extensions.Logging;
using Shizuka.Sources.Hosting;
using Api = Shizuka.Sources.Abstractions;

namespace Convy.Services.Extensions;

/// <summary>
/// What every source of an extension gets for a call, as Shizuka gives it: HTTP on the manifest's domains and the hosts
/// of its Url settings, with the source's cookies, rate gate, proxy and timeout; the settings of
/// <c>extensions.settings.&lt;id&gt;.values</c> checked by their kind; the service of checks (<c>captcha</c>) in the
/// source's own profile there. A source a provider listed takes the provider's settings, proxy and hosts.
/// </summary>
public abstract class ExtensionUnit : ISourceUnit
{
    private readonly ExtensionUnit? _settingsOf;
    private readonly RateGate _gate;
    private readonly object _lock = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private SourceCookieJar? _cookies;
    private (Uri Url, string Key, string Profile, CaptchaSolverClient Client)? _solver;

    /// <param name="settingsOf">The provider that listed the source: its settings are the source's; <c>null</c> — the source's own.</param>
    protected ExtensionUnit(Api.SourceManifest manifest, string package, ExtensionHost host, ILogger logger, ExtensionUnit? settingsOf = null)
    {
        Extension = manifest;
        Package = package;
        Host = host;
        Logger = logger;
        _settingsOf = settingsOf;
        _gate = new RateGate(host.Time);
    }

    public string Id => Extension.Id;

    /// <summary>The extension package the source comes from.</summary>
    public string Package { get; }

    /// <summary>The source's manifest as the extension wrote it.</summary>
    public Api.SourceManifest Extension { get; }

    Api.SourceManifest ISourceUnit.Manifest => Extension;

    /// <summary>Whose settings the source has: its own id, or the provider's that listed it.</summary>
    public string SettingsId => _settingsOf?.SettingsId ?? Id;

    protected ExtensionHost Host { get; }

    protected ILogger Logger { get; }

    /// <summary>The manifest whose settings the source has.</summary>
    private Api.SourceManifest SettingsManifest => _settingsOf?.SettingsManifest ?? Extension;

    /// <summary>A context for one call of the source, with its HTTP.</summary>
    protected (SourceContext Context, SourceHttp Http) NewCall(TimeSpan? timeout = null)
    {
        var entry = Host.Options.For(SettingsId);
        var saved = Saved(entry);
        var http = Http(saved, entry, timeout);
        var context = new SourceContext(http, Host.Html, new SourceSettingsView(SettingsManifest, saved), new Journal(Logger, Id));
        return (context, http);
    }

    /// <summary>
    /// Runs one call of the extension with a context made for it. A check the extension reported itself (the site's
    /// own captcha) is handed to the service of checks, and once passed the call runs once more. The extension's
    /// failures become <see cref="SourceException"/>s of the same kind; a check the service did not pass names the one
    /// that waits for a person (<see cref="SourceException.ChallengeId"/>).
    /// </summary>
    protected async Task<T> CallAsync<T>(Func<Api.ISourceContext, CancellationToken, Task<T>> call, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var (context, http) = NewCall(timeout);
        try
        {
            try
            {
                return await call(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Api.SourceException ex) when (ex is { Kind: Api.SourceErrorKind.Challenge, ChallengeUrl: not null }
                                                 && http is { PassesChallenges: true, Challenge: null, ChallengeProblem: null })
            {
                if (!await http.PassSiteAsync(ex.ChallengeUrl, ex.ChallengeDoneWhen, cancellationToken).ConfigureAwait(false))
                {
                    ExceptionDispatchInfo.Throw(ex);
                }
            }

            return await call(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Api.SourceException ex)
        {
            throw Failure(ex, http);
        }
        catch (NotSupportedException ex)
        {
            throw new SourceException(SourceErrorKind.Error, ex.Message, ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SourceException))
        {
            Logger.LogError(ex, "Source {Source} of {Package} threw", Id, Package);
            throw new SourceException(SourceErrorKind.Error, $"{Extension.Name} failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>Convy's error for the extension's: of the same kind; a check says whether one waits for a person.</summary>
    protected static SourceException Failure(Api.SourceException ex, SourceHttp http)
    {
        if (ex.Kind == Api.SourceErrorKind.AuthFailed)
        {
            return new SourceException(SourceErrorKind.AuthFailed, ex.Message, ex);
        }

        if (ex.Kind != Api.SourceErrorKind.Challenge)
        {
            return new SourceException(SourceErrorKind.Error, ex.Message, ex);
        }

        (string Message, string? Id) said = !http.PassesChallenges
            ? (ex.Message + " No service of checks is set up (captcha.url, captcha.key).", null)
            : http.ChallengeProblem is { } problem
                ? ($"{ex.Message} The service of checks failed: {problem}", null)
                : http.Challenge switch
                {
                    { Status: ChallengeStatus.Pending } pending => (ex.Message + " The check waits for someone to pass it.", pending.Id),
                    { } failed => ($"{ex.Message} The check was not passed{(failed.Message is { } why ? $": {why}" : "")}.", null),
                    null => (ex.Message, null),
                };
        return new SourceException(SourceErrorKind.Challenge, said.Message, ex)
        {
            ChallengeUrl = http.Challenge?.Url ?? ex.ChallengeUrl,
            ChallengeId = said.Id,
        };
    }

    /// <summary>
    /// The plain connection of the source's proxy, whether it may go to a host (one of the manifest's domains or of its
    /// Url settings' hosts), its cookies and its timeout. For what <see cref="SourceHttp"/> does not do (a redirect to a
    /// <c>magnet:</c> link).
    /// </summary>
    protected (HttpClient Client, Func<Uri, bool> Covers, System.Net.CookieContainer Cookies, TimeSpan Timeout) Direct()
    {
        var entry = Host.Options.For(SettingsId);
        var saved = Saved(entry);
        var hosts = SettingValues.UrlHosts(SettingsManifest, saved);
        return (Host.Transport(entry.Proxy).Client,
            url => url.Scheme is "http" or "https" && (Extension.Covers(url.Host) || hosts.Any(h => string.Equals(h, url.Host, StringComparison.OrdinalIgnoreCase))),
            Cookies(Extension.Domains.Concat(hosts).ToList(), saved),
            TimeSpan.FromSeconds(Math.Max(5, entry.TimeoutSeconds)));
    }

    /// <summary>HTTP of one call: the manifest's domains and those of the Url settings, the gate and cookies, the proxy and timeout.</summary>
    protected SourceHttp NewHttp(TimeSpan? timeout = null)
    {
        var entry = Host.Options.For(SettingsId);
        return Http(Saved(entry), entry, timeout);
    }

    private SourceHttp Http(Dictionary<string, string> saved, ExtensionEntryOptions entry, TimeSpan? timeout)
    {
        var hosts = SettingValues.UrlHosts(SettingsManifest, saved);
        var time = timeout ?? TimeSpan.FromSeconds(Math.Max(5, entry.TimeoutSeconds));
        var limits = new SourceHttpLimits
        {
            RequestTimeout = time,
            FetchTimeout = time,
            ChallengeWait = CallBudget.Cut(TimeSpan.FromSeconds(Math.Clamp(Host.Captcha.CurrentValue.WaitSeconds, 0, 60))),
        };
        return new SourceHttp(this, hosts, Host.Transport(entry.Proxy), _gate, Cookies(Extension.Domains.Concat(hosts).ToList(), saved),
            new Journal(Logger, Id), limits, challenges: Solver());
    }

    /// <summary>The source's cookie jar: made again when its Cookies or Url settings change.</summary>
    private System.Net.CookieContainer Cookies(IReadOnlyList<string> domains, Dictionary<string, string> saved)
    {
        var seed = new StringBuilder();
        foreach (var setting in SettingsManifest.Settings.Where(s => s.Kind is Api.SourceSettingKind.Cookies or Api.SourceSettingKind.Url))
        {
            if (saved.TryGetValue(setting.Key, out var value))
            {
                seed.Append(setting.Key).Append('=').Append(value).Append('\n');
            }
        }

        lock (_lock)
        {
            if (_cookies is null || _cookies.Seed != seed.ToString())
            {
                _cookies = SourceCookieJar.Create(domains, seed.ToString());
            }

            return _cookies.Container;
        }
    }

    /// <summary>The service of checks in this source's own profile there (<c>&lt;domain&gt;#convy.&lt;id&gt;</c>); null — none set up.</summary>
    private CaptchaSolverClient? Solver()
    {
        var options = Host.Captcha.CurrentValue;
        if (options.Upstream is not { } upstream)
        {
            return null;
        }

        var profile = options.ProfileFor(Id);
        lock (_lock)
        {
            if (_solver is not { } known || known.Url != upstream.Url || known.Key != upstream.Key || known.Profile != profile)
            {
                _solver = known = (upstream.Url, upstream.Key, profile, new CaptchaSolverClient(Host.CaptchaHttp, upstream.Url, upstream.Key, profile));
            }

            return known.Client;
        }
    }

    /// <summary>The values of <c>values</c> the manifest has a setting for, checked and normalized by its kind; others are left out with a warning.</summary>
    private Dictionary<string, string> Saved(ExtensionEntryOptions entry)
    {
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entry.Values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var setting = SettingsManifest.Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
            if (setting is null)
            {
                WarnOnce(key, value, $"extensions.settings.{SettingsId}.values.{key}: {SettingsManifest.Name} has no such setting.");
                continue;
            }

            if (SettingValues.Normalize(setting, value.Trim(), out var error) is { } normalized)
            {
                saved[setting.Key] = normalized;
            }
            else
            {
                WarnOnce(key, value, $"extensions.settings.{SettingsId}.values.{key} is left out: {error}");
            }
        }

        return saved;
    }

    /// <summary>A warning about a setting's value, once for each value: settings are read on every call.</summary>
    private void WarnOnce(string key, string value, string warning)
    {
        lock (_lock)
        {
            if (!_warned.Add($"{key}\n{value}"))
            {
                return;
            }
        }

        Logger.LogWarning("{Warning}", warning);
    }

    /// <summary>The source's notes and the answers of its requests, into Convy's log under the source's id.</summary>
    private sealed class Journal(ILogger logger, string source) : Api.ISourceLog, ISourceHttpJournal
    {
        public void Write(Api.SourceLogLevel level, string message, object? data = null) =>
            logger.Log(level switch
            {
                Api.SourceLogLevel.Warning => LogLevel.Warning,
                Api.SourceLogLevel.Error => LogLevel.Error,
                _ => LogLevel.Information,
            }, "[{Source}] {Message} {@Data}", source, message, data);

        public Task ResponseAsync(string request, int? statusCode, string body, CancellationToken ct)
        {
            logger.LogDebug("[{Source}] {Request} → {Status}", source, request, statusCode);
            return Task.CompletedTask;
        }
    }
}
