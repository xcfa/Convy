namespace Convy.Services.Captcha;

/// <summary>
/// Tells a bot check a browser can pass (Cloudflare's «Just a moment...», DDoS-Guard's check) from an ordinary answer, as
/// captcha-solver's detector does. A hard block is not one, and a site's own captcha is the plugin's to report.
/// </summary>
public static class ChallengeDetector
{
    // Cloudflare puts /cdn-cgi/challenge-platform/ into ordinary pages too, so that path alone means nothing.
    private static readonly string[] CloudflareMarkers =
        ["<title>Just a moment...</title>", "window._cf_chl_opt", "id=\"challenge-form\"", "cf-chl-widget"];

    // Its block page carries the same title as the check: only the check's script counts.
    private static readonly string[] DdosGuardMarkers = [".well-known/ddos-guard/js-challenge", ".well-known/ddos-guard/check"];

    /// <summary>Whether an answer's status and headers may be a check: only then is its body worth reading.</summary>
    public static bool MayBe(int status, Func<string, string> header) =>
        header("cf-mitigated").Contains("challenge", StringComparison.OrdinalIgnoreCase)
        || status is 403 or 429 or 503 && (header("server").Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                                           || header("server").Contains("ddos-guard", StringComparison.OrdinalIgnoreCase));

    /// <summary>An answer with <paramref name="status"/>, headers read by <paramref name="header"/> and the start of its <paramref name="body"/>.</summary>
    public static bool IsChallenge(int status, Func<string, string> header, string? body)
    {
        if (header("cf-mitigated").Contains("challenge", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (status is not (403 or 429 or 503) || body is null)
        {
            return false;
        }

        var server = header("server");
        return server.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) && Contains(body, CloudflareMarkers)
               || server.Contains("ddos-guard", StringComparison.OrdinalIgnoreCase) && Contains(body, DdosGuardMarkers);
    }

    /// <summary>The same for an <see cref="HttpResponseMessage"/> whose body is <paramref name="body"/>.</summary>
    public static bool IsChallenge(HttpResponseMessage response, string? body) =>
        IsChallenge((int)response.StatusCode, Header(response), body);

    /// <summary>A response's header (its content's too), its values joined; empty when absent.</summary>
    public static Func<string, string> Header(HttpResponseMessage response) => name =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : string.Empty;

    private static bool Contains(string body, string[] markers) =>
        markers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase));
}
