using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Convy.Services.Downloaders.Opds;

/// <summary>Builds the HTTP client of an OPDS catalog.</summary>
public static class OpdsHttp
{
    /// <summary>
    /// A client for <paramref name="options"/>: its proxy (e.g. <c>socks5://tor:9050</c>; the
    /// environment's proxy otherwise), basic authentication when a user name is set, redirects
    /// followed (Flibusta sends files through one). Timeouts are left to the callers' tokens.
    /// </summary>
    public static HttpClient Create(OpdsCatalogOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };

        if (!string.IsNullOrWhiteSpace(options.Proxy))
        {
            handler.Proxy = new WebProxy(new Uri(options.Proxy.Trim()));
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Convy/1.0 (OPDS client)");

        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return client;
    }
}
