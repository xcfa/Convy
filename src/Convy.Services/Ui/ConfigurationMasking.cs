using System.Text.RegularExpressions;

namespace Convy.Services.Ui;

/// <summary>Hides secrets in configuration values before they are shown.</summary>
public static partial class ConfigurationMasking
{
    public const string Hidden = "••••••";

    /// <summary>
    /// The value to show for <paramref name="key"/>: hidden entirely when the key names a secret
    /// (password, API key, client secret, token, …); otherwise with credentials inside it hidden
    /// (<c>user:password@</c> in a URL, <c>?token=</c>-like query parameters, <c>Password=</c> in
    /// a connection string). <paramref name="hidden"/> tells whether anything was hidden.
    /// </summary>
    public static string? Mask(string key, string? value, out bool hidden)
    {
        hidden = false;
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var name = key[(key.LastIndexOf(':') + 1)..];
        if (SecretKey().IsMatch(name) || IsHeaderValue(key) || IsSourceValue(key) || IsIndexUser(key))
        {
            hidden = true;
            return Hidden;
        }

        var masked = UrlCredentials().Replace(value, m => $"{m.Groups["user"].Value}:{Hidden}@");
        masked = SecretQuery().Replace(masked, m => $"{m.Groups["name"].Value}={Hidden}");
        masked = ConnectionPassword().Replace(masked, m => $"{m.Groups["name"].Value}={Hidden}");

        hidden = !string.Equals(masked, value, StringComparison.Ordinal);
        return masked;
    }

    /// <summary>A webhook header's value (<c>Webhooks:0:headers:1:value</c>): usually a token under a neutral name.</summary>
    private static bool IsHeaderValue(string key)
    {
        var segments = key.Split(':');
        return segments.Length >= 3
               && string.Equals(segments[^1], "value", StringComparison.OrdinalIgnoreCase)
               && string.Equals(segments[^3], "headers", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A source's own setting (<c>Extensions:settings:e-hentai:values:cookies</c>): only its manifest knows whether it is a
    /// secret, and the Sources page shows the others.
    /// </summary>
    private static bool IsSourceValue(string key)
    {
        var segments = key.Split(':');
        return segments.Length == 5
               && string.Equals(segments[0], "Extensions", StringComparison.OrdinalIgnoreCase)
               && string.Equals(segments[1], "settings", StringComparison.OrdinalIgnoreCase)
               && string.Equals(segments[3], "values", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The user name of an index of packages (<c>Extensions:ui_indexes:0:username</c>): a public share's token, for one,
    /// is all its secret.
    /// </summary>
    private static bool IsIndexUser(string key)
    {
        var segments = key.Split(':');
        return segments.Length == 4
               && string.Equals(segments[0], "Extensions", StringComparison.OrdinalIgnoreCase)
               && (string.Equals(segments[1], "indexes", StringComparison.OrdinalIgnoreCase) || string.Equals(segments[1], "ui_indexes", StringComparison.OrdinalIgnoreCase))
               && string.Equals(segments[3], "username", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"(password|passwd|pwd|secret|token|credentials?|key|cookies?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    [GeneratedRegex(@"(?<=://)(?<user>[^/@:\s]+):(?<password>[^/@\s]+)@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?<=[?&])(?<name>[^=&#\s]*(?:token|key|password|passwd|secret|sig|signature|auth)[^=&#\s]*)=(?<value>[^&#\s]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretQuery();

    [GeneratedRegex(@"(?<name>\b(?:password|pwd))\s*=\s*(?<value>[^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionPassword();
}
