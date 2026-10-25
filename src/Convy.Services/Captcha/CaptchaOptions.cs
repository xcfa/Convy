using Microsoft.Extensions.Configuration;

namespace Convy.Services.Captcha;

/// <summary>
/// The <c>captcha</c> section of configuration.yml: the service of checks (captcha-solver) that passes the Cloudflare and
/// DDoS-Guard checks and the sites' captchas the plugins run into. Read on every request, so an edit applies at once.
/// </summary>
public sealed class CaptchaOptions
{
    public const string SectionName = "Captcha";

    /// <summary>The service's address, e.g. <c>http://captcha-solver:8191/</c>; empty — no service, a check is an error.</summary>
    [ConfigurationKeyName("url")]
    public string? Url { get; set; }

    /// <summary>Convy's key at the service, with the <c>solve</c> and <c>solver</c> scopes (a Docker secret: <c>Captcha__key</c>).</summary>
    [ConfigurationKeyName("key")]
    public string? Key { get; set; }

    /// <summary>
    /// How long a request waits for the service to pass a check before the check is the request's error (at most 60 s).
    /// A call of the REST API waits only as long as its own time allows, so the client still hears about the check.
    /// </summary>
    [ConfigurationKeyName("wait_sec")]
    public double WaitSeconds { get; set; } = 25;

    /// <summary>
    /// The name of Convy's own browser profiles at the service: checks run in <c>&lt;host&gt;#&lt;profile&gt;</c>, with the
    /// plugin's cookies (a sign-in), which never reach the profile other clients share.
    /// </summary>
    [ConfigurationKeyName("profile")]
    public string Profile { get; set; } = "convy";

    /// <summary>The service, when both its address and the key are set.</summary>
    public (Uri Url, string Key)? Upstream =>
        !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Key)
        && Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? (url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/"), Key.Trim())
            : null;
}
