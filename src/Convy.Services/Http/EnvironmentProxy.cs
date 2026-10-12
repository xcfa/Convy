using System.Net;

namespace Convy.Services.Http;

/// <summary>
/// The proxy of the standard environment variables, read the way curl, Go and most tools read
/// them. Convy sets it as <see cref="HttpClient.DefaultProxy"/>, so every client uses it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>https://</c> addresses use <c>HTTPS_PROXY</c> and <c>http://</c> ones <c>HTTP_PROXY</c>;
/// <c>ALL_PROXY</c> covers both. A lower-case name wins over the upper-case one.</item>
/// <item>A proxy without a scheme is <c>http://</c>; <c>socks5h://</c> is taken as <c>socks5://</c>,
/// which in .NET lets the proxy resolve names as well. <c>user:password@</c> is sent to the proxy.</item>
/// <item><c>NO_PROXY</c> is read by <see cref="NoProxyList"/>; <c>localhost</c> and loopback
/// addresses are never proxied.</item>
/// </list>
/// </remarks>
public sealed class EnvironmentProxy : IWebProxy
{
    private EnvironmentProxy(Uri? httpProxy, Uri? httpsProxy, NoProxyList bypass, ICredentials? credentials)
    {
        HttpProxy = httpProxy;
        HttpsProxy = httpsProxy;
        Bypass = bypass;
        Credentials = credentials;
    }

    /// <summary>The proxy of <c>http://</c> addresses, without credentials.</summary>
    public Uri? HttpProxy { get; }

    /// <summary>The proxy of <c>https://</c> addresses, without credentials.</summary>
    public Uri? HttpsProxy { get; }

    public NoProxyList Bypass { get; }

    public ICredentials? Credentials { get; set; }

    /// <summary>
    /// The proxy the variables name, or <c>null</c> when they name none. <paramref name="problems"/>
    /// gets what could not be read: a proxy that is not an address, <c>NO_PROXY</c> entries.
    /// </summary>
    /// <param name="variable">Reads a variable, e.g. <see cref="Environment.GetEnvironmentVariable(string)"/>.</param>
    public static EnvironmentProxy? FromEnvironment(Func<string, string?> variable, out IReadOnlyList<string> problems)
    {
        var found = new List<string>();

        string? Read(string name) =>
            variable(name.ToLowerInvariant()) is { Length: > 0 } lower ? lower.Trim()
            : variable(name) is { Length: > 0 } upper ? upper.Trim()
            : null;

        Uri? Proxy(string name)
        {
            if (Read(name) is not { } value)
            {
                return null;
            }

            if (TryParseProxy(value, out var proxy))
            {
                return proxy;
            }

            found.Add($"{name} is not a proxy address such as http://proxy:3128 or socks5://proxy:1080.");
            return null;
        }

        var all = Proxy("ALL_PROXY");
        var http = Proxy("HTTP_PROXY") ?? all;
        var https = Proxy("HTTPS_PROXY") ?? all;
        var bypass = NoProxyList.Parse(Read("NO_PROXY"));
        found.AddRange(bypass.Invalid.Select(e => $"NO_PROXY entry '{e}' is not a host, domain, address or address range; it is ignored."));
        problems = found;

        if (http is null && https is null)
        {
            return null;
        }

        var credentials = new Dictionary<string, NetworkCredential>(StringComparer.OrdinalIgnoreCase);
        foreach (var proxy in new[] { http, https }.OfType<Uri>().Where(p => p.UserInfo.Length > 0))
        {
            var userInfo = proxy.UserInfo;
            var colon = userInfo.IndexOf(':');
            credentials[Key(proxy)] = new NetworkCredential(
                Uri.UnescapeDataString(colon < 0 ? userInfo : userInfo[..colon]),
                colon < 0 ? string.Empty : Uri.UnescapeDataString(userInfo[(colon + 1)..]));
        }

        return new EnvironmentProxy(
            WithoutCredentials(http), WithoutCredentials(https), bypass,
            credentials.Count > 0 ? new ProxyCredentials(credentials) : null);
    }

    public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? null : ProxyFor(destination);

    public bool IsBypassed(Uri host) => ProxyFor(host) is null || host.IsLoopback || Bypass.Matches(host);

    private Uri? ProxyFor(Uri destination) => destination.Scheme switch
    {
        "https" or "wss" => HttpsProxy,
        "http" or "ws" => HttpProxy,
        _ => null,
    };

    private static bool TryParseProxy(string value, out Uri proxy)
    {
        var text = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        if (text.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase))
        {
            text = "socks5://" + text["socks5h://".Length..];
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https" or "socks4" or "socks4a" or "socks5"
            && parsed.Host.Length > 0)
        {
            proxy = parsed;
            return true;
        }

        proxy = null!;
        return false;
    }

    private static Uri? WithoutCredentials(Uri? proxy) =>
        proxy is null ? null : new Uri($"{proxy.Scheme}://{proxy.Authority}");

    private static string Key(Uri proxy) => $"{proxy.Host}:{proxy.Port}";

    /// <summary>The credentials of each proxy, by host and port.</summary>
    private sealed class ProxyCredentials(IReadOnlyDictionary<string, NetworkCredential> byProxy) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) => byProxy.GetValueOrDefault(Key(uri));
    }
}
