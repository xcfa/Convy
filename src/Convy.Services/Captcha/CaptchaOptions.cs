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
    /// The name of Convy's own browser profiles at the service: a plugin's checks run in
    /// <c>&lt;domain&gt;#&lt;profile&gt;.&lt;plugin&gt;</c>, with its cookies (a sign-in), which never reach other
    /// plugins, Convy's clients or the profile other clients of the service share.
    /// </summary>
    [ConfigurationKeyName("profile")]
    public string Profile { get; set; } = "convy";

    /// <summary>The profile name of a plugin's checks (letters, digits, '.', '_', '-'; up to 64, as the service takes).</summary>
    public string ProfileFor(string plugin) => Clean($"{OwnName}.{plugin}");

    /// <summary>Whether <paramref name="profile"/> (<c>domain#name</c>) is one of Convy's own: its clients never get into them.</summary>
    public bool IsOwn(string? profile)
    {
        var hash = profile?.IndexOf('#', StringComparison.Ordinal) ?? -1;
        if (hash < 0)
        {
            return false;
        }

        var name = profile![(hash + 1)..].Trim().ToLowerInvariant();
        return name == OwnName || name.StartsWith(OwnName + ".", StringComparison.Ordinal);
    }

    private string OwnName => Clean(string.IsNullOrWhiteSpace(Profile) ? "convy" : Profile);

    private static string Clean(string name)
    {
        var clean = new string(name.Trim().ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '-').ToArray());
        return clean.Length > 64 ? clean[..64] : clean;
    }

    /// <summary>The service, when both its address and the key are set.</summary>
    public (Uri Url, string Key)? Upstream =>
        !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Key)
        && Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? (url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/"), Key.Trim())
            : null;
}
