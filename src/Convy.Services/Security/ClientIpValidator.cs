using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Security;

/// <inheritdoc />
public sealed class ClientIpValidator : IClientIpValidator, IDisposable
{
    private readonly ILogger<ClientIpValidator> _logger;
    private readonly IDisposable? _changeSubscription;

    // Swapped atomically on configuration reload; read without locking.
    private volatile IPNetwork[] _networks;

    public ClientIpValidator(
        IOptionsMonitor<IpAccessControlOptions> options,
        ILogger<ClientIpValidator> logger)
    {
        _logger = logger;
        _networks = Parse(options.CurrentValue.AllowedNetworks);
        _changeSubscription = options.OnChange(o => _networks = Parse(o.AllowedNetworks));
    }

    /// <inheritdoc />
    public bool IsAllowed(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        // Normalize IPv4-mapped IPv6 (e.g. ::ffff:127.0.0.1) so it matches IPv4 ranges.
        var address = remoteAddress.IsIPv4MappedToIPv6
            ? remoteAddress.MapToIPv4()
            : remoteAddress;

        var networks = _networks;
        foreach (var network in networks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    private IPNetwork[] Parse(IEnumerable<string> entries)
    {
        var result = new List<IPNetwork>();

        foreach (var entry in entries)
        {
            if (TryParse(entry, out var network))
            {
                result.Add(network);
            }
            else
            {
                _logger.LogWarning("Ignoring invalid IP access-control entry '{Entry}'.", entry);
            }
        }

        return result.ToArray();
    }

    private static bool TryParse(string entry, out IPNetwork network)
    {
        entry = entry.Trim();

        if (entry.Contains('/'))
        {
            return IPNetwork.TryParse(entry, out network);
        }

        if (IPAddress.TryParse(entry, out var address))
        {
            var prefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            network = new IPNetwork(address, prefix);
            return true;
        }

        network = default;
        return false;
    }

    public void Dispose() => _changeSubscription?.Dispose();
}
