using System.Net;

namespace Convy.Services.Security;

/// <summary>Decides whether a client IP address is allowed to access the API.</summary>
public interface IClientIpValidator
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="remoteAddress"/> falls within one of
    /// the configured allowed networks. A <c>null</c> address is rejected.
    /// </summary>
    bool IsAllowed(IPAddress? remoteAddress);
}
