namespace Convy.Services.Security;

/// <summary>
/// Controls which client IP addresses may reach the API. Each entry is a single
/// IP (<c>127.0.0.1</c>, <c>::1</c>) or a CIDR range (<c>192.168.0.0/16</c>).
/// Requests from any other address are rejected.
/// </summary>
public class IpAccessControlOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "IpAccessControl";

    /// <summary>
    /// Defaults used when nothing is configured: IPv4/IPv6 loopback, the common
    /// private LAN range, and the Docker bridge range. <c>172.16.0.0/12</c> covers
    /// the default <c>docker0</c> bridge (172.17.0.0/16) as well as user-defined
    /// bridge networks (172.18+.0.0/16).
    /// </summary>
    public static IReadOnlyList<string> DefaultAllowedNetworks { get; } = new[]
    {
        "127.0.0.1",
        "::1",
        "192.168.0.0/16",
        "172.16.0.0/12",
    };

    /// <summary>Allowed single IPs and/or CIDR ranges.</summary>
    public List<string> AllowedNetworks { get; set; } = new();
}
