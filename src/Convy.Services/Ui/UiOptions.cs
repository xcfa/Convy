namespace Convy.Services.Ui;

/// <summary>How the web UI is protected.</summary>
public enum UiMode
{
    /// <summary>The UI is off: OIDC is not configured and open access was not asked for.</summary>
    Disabled,

    /// <summary>Users sign in with OpenID Connect (Authelia or any other provider).</summary>
    Oidc,

    /// <summary>No sign-in: anyone the IP allow-list lets through can use the UI.</summary>
    Open,
}

/// <summary>The <c>Ui</c> configuration section.</summary>
public sealed class UiOptions
{
    public const string SectionName = "Ui";

    /// <summary><c>oidc</c> (the default) or <c>none</c> for open access.</summary>
    public string Auth { get; set; } = "oidc";

    /// <summary>
    /// The address users open Convy at, e.g. <c>https://convy.example.com</c>. Behind a reverse
    /// proxy it makes the sign-in callback and cookies use the public scheme and host.
    /// </summary>
    public string? PublicUrl { get; set; }

    public UiOidcOptions Oidc { get; set; } = new();

    /// <summary>The mode these settings ask for; <paramref name="problem"/> says why the UI is off.</summary>
    public UiMode ResolveMode(out string? problem)
    {
        problem = null;

        if (string.Equals(Auth, "none", StringComparison.OrdinalIgnoreCase))
        {
            return UiMode.Open;
        }

        if (!string.Equals(Auth, "oidc", StringComparison.OrdinalIgnoreCase))
        {
            problem = $"Ui:Auth must be 'oidc' or 'none', not '{Auth}'.";
            return UiMode.Disabled;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Oidc.Authority)) missing.Add("Ui:Oidc:Authority");
        if (string.IsNullOrWhiteSpace(Oidc.ClientId)) missing.Add("Ui:Oidc:ClientId");
        if (string.IsNullOrWhiteSpace(Oidc.ClientSecret)) missing.Add("Ui:Oidc:ClientSecret");

        if (missing.Count > 0)
        {
            problem = $"OIDC is not configured ({string.Join(", ", missing)} missing).";
            return UiMode.Disabled;
        }

        if (PublicUrl is not null && !TryGetPublicOrigin(out _))
        {
            problem = $"Ui:PublicUrl '{PublicUrl}' is not an absolute http(s) address without a path.";
            return UiMode.Disabled;
        }

        return UiMode.Oidc;
    }

    /// <summary>
    /// Where the browser goes after signing out: the provider's logout page (asked to send
    /// the user back to <paramref name="origin"/>) when one is configured, else Convy's own page.
    /// </summary>
    public string SignedOutRedirect(Uri origin)
    {
        if (string.IsNullOrWhiteSpace(Oidc.LogoutUrl))
        {
            return "/auth/signed-out";
        }

        var logout = Oidc.LogoutUrl.Trim();
        var separator = logout.Contains('?') ? '&' : '?';
        return $"{logout}{separator}rd={Uri.EscapeDataString(new Uri(origin, "/").ToString())}";
    }

    /// <summary>The scheme and host of <see cref="PublicUrl"/>; Convy must be served at the root of that host.</summary>
    public bool TryGetPublicOrigin(out Uri origin)
    {
        origin = null!;
        if (string.IsNullOrWhiteSpace(PublicUrl)
            || !Uri.TryCreate(PublicUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.AbsolutePath.Trim('/').Length > 0
            || !string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        origin = new Uri(uri.GetLeftPart(UriPartial.Authority));
        return true;
    }
}

/// <summary>The <c>Ui:Oidc</c> configuration section.</summary>
public sealed class UiOidcOptions
{
    /// <summary>The provider's address, e.g. <c>https://auth.example.com</c> for Authelia.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>The client secret; pass it as a Docker secret (<c>Ui__Oidc__ClientSecret</c>).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Scopes to ask for. <c>groups</c> is needed for <see cref="AllowedGroups"/>.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>When not empty, only members of one of these groups may use the UI.</summary>
    public List<string> AllowedGroups { get; set; } = [];

    /// <summary>Whether the provider must be reached over HTTPS. Only turn off for local tests.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Where "Sign out" sends the browser after Convy's own session ends, e.g.
    /// <c>https://auth.example.com/logout</c> to end the Authelia session too. Convy adds
    /// <c>rd=&lt;public url&gt;</c>. Without it only Convy's session ends.
    /// </summary>
    public string? LogoutUrl { get; set; }

    /// <summary>The scopes actually requested: the configured ones, or the default set.</summary>
    public IReadOnlyList<string> EffectiveScopes =>
        Scopes.Count > 0
            ? Scopes.Append("openid").Distinct(StringComparer.Ordinal).ToList()
            : ["openid", "profile", "email", "groups"];

    /// <summary>Whether a user with <paramref name="groups"/> may use the UI.</summary>
    public bool Allows(IEnumerable<string> groups) =>
        AllowedGroups.Count == 0 || groups.Any(g => AllowedGroups.Contains(g, StringComparer.Ordinal));
}
