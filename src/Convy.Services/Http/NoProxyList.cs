using System.Globalization;
using System.Net;

namespace Convy.Services.Http;

/// <summary>
/// <c>NO_PROXY</c> read the way curl, Go and most tools read it. Entries are separated by commas
/// (or spaces); each is one of:
/// <list type="bullet">
/// <item><c>*</c>: nothing is proxied;</item>
/// <item><c>example.org</c>, <c>.example.org</c> or <c>*.example.org</c>: that domain and all its subdomains;</item>
/// <item><c>10.0.0.5</c>, <c>fd00::5</c>: that address;</item>
/// <item><c>172.16.0.0/12</c>, <c>fd00::/8</c>: an address range (host names are not resolved to match it);</item>
/// <item>a domain or address with <c>:port</c> (<c>[fd00::5]:8080</c> for IPv6): only that port.</item>
/// </list>
/// Case does not matter and a trailing dot is ignored. .NET's own reading understands only exact
/// hosts and <c>.domain</c> suffixes, which is why Convy reads the variable itself.
/// </summary>
public sealed class NoProxyList
{
    private static readonly char[] Separators = [',', ' ', '\t', '\r', '\n'];

    private readonly IReadOnlyList<Entry> _entries;

    private NoProxyList(IReadOnlyList<Entry> entries, bool all, IReadOnlyList<string> invalid)
    {
        _entries = entries;
        All = all;
        Invalid = invalid;
    }

    /// <summary>The list contains <c>*</c>: nothing is proxied.</summary>
    public bool All { get; }

    /// <summary>Entries that are none of the forms above (e.g. <c>10.0.0.0/40</c>); they match nothing.</summary>
    public IReadOnlyList<string> Invalid { get; }

    public static NoProxyList Parse(string? value)
    {
        var entries = new List<Entry>();
        var invalid = new List<string>();
        var all = false;

        foreach (var raw in (value ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == "*")
            {
                all = true;
            }
            else if (TryParseEntry(raw.ToLowerInvariant(), out var entry))
            {
                entries.Add(entry);
            }
            else
            {
                invalid.Add(raw);
            }
        }

        return new NoProxyList(entries, all, invalid);
    }

    /// <summary>Whether requests to <paramref name="destination"/> go direct.</summary>
    public bool Matches(Uri destination)
    {
        if (All)
        {
            return true;
        }

        var host = destination.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        var address = IPAddress.TryParse(host, out var parsed) ? Normalize(parsed) : null;

        foreach (var entry in _entries)
        {
            if (entry.Port is { } port && port != destination.Port)
            {
                continue;
            }

            var matches = entry switch
            {
                { Domain: { } domain } => address is null && (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal)),
                { Network: { } network } => address is not null && InRange(address, network, entry.PrefixLength),
                _ => false,
            };

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseEntry(string text, out Entry entry)
    {
        entry = default;
        int? port = null;
        var host = text;

        // Port: [v6]:port, name:port or v4:port; a bare IPv6 address has several colons and no port.
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            var rest = host[(close + 1)..];
            host = host[1..close];
            if (rest.Length > 0 && !TryParsePort(rest, out port))
            {
                return false;
            }
        }
        else if (host.Count(c => c == ':') == 1 && !host.Contains('/'))
        {
            var colon = host.IndexOf(':');
            if (!TryParsePort(host[colon..], out port))
            {
                return false;
            }

            host = host[..colon];
        }

        var slash = host.IndexOf('/');
        if (slash >= 0)
        {
            if (!IPAddress.TryParse(host[..slash], out var network)
                || !int.TryParse(host[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bits))
            {
                return false;
            }

            network = Normalize(network);
            if (bits > network.GetAddressBytes().Length * 8)
            {
                return false;
            }

            entry = new Entry(null, network, bits, port);
            return true;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            address = Normalize(address);
            entry = new Entry(null, address, address.GetAddressBytes().Length * 8, port);
            return true;
        }

        var domain = host.StartsWith("*.", StringComparison.Ordinal) ? host[2..]
            : host.StartsWith('.') ? host[1..]
            : host;
        domain = domain.TrimEnd('.');
        if (Uri.CheckHostName(domain) != UriHostNameType.Dns)
        {
            return false;
        }

        entry = new Entry(domain, null, 0, port);
        return true;
    }

    private static bool TryParsePort(string text, out int? port)
    {
        port = null;
        if (text.Length < 2 || text[0] != ':'
            || !int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value is < 1 or > 65535)
        {
            return false;
        }

        port = value;
        return true;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool InRange(IPAddress address, IPAddress network, int prefixLength)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        for (var i = 0; i < a.Length && prefixLength > 0; i++, prefixLength -= 8)
        {
            var mask = prefixLength >= 8 ? 0xFF : 0xFF << (8 - prefixLength) & 0xFF;
            if ((a[i] & mask) != (n[i] & mask))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A domain with its subdomains, or an address range (a single address is a full-length range).</summary>
    private readonly record struct Entry(string? Domain, IPAddress? Network, int PrefixLength, int? Port);
}
