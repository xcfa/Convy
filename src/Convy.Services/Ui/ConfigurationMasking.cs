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
        if (SecretKey().IsMatch(name))
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

    [GeneratedRegex(@"(password|passwd|pwd|secret|token|credentials?|key)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    [GeneratedRegex(@"(?<=://)(?<user>[^/@:\s]+):(?<password>[^/@\s]+)@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?<=[?&])(?<name>[^=&#\s]*(?:token|key|password|passwd|secret|sig|signature|auth)[^=&#\s]*)=(?<value>[^&#\s]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretQuery();

    [GeneratedRegex(@"(?<name>\b(?:password|pwd))\s*=\s*(?<value>[^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionPassword();
}
