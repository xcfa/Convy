namespace Convy.Services.Ui;

/// <summary>Request checks of the web UI that do not depend on the web framework.</summary>
public static class UiRequestRules
{
    /// <summary>
    /// Whether a request must carry the UI's own header: every state-changing request to the
    /// UI's API or its sign-in endpoints. Browsers do not let another site add custom headers
    /// to a cross-origin request, so this stops cross-site form posts even from sibling hosts
    /// that share the session cookie's site.
    /// </summary>
    public static bool NeedsUiHeader(string method, string path)
    {
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsUnder(path, "/api/ui") || IsUnder(path, "/auth");
    }

    private static bool IsUnder(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');
}
