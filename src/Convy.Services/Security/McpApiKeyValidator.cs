using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Convy.Services.Security;

/// <summary>Access settings of the MCP endpoint (<c>MCP__APIKEY</c>).</summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>Key the agent must present; without it <c>/mcp</c> refuses every request.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Outcome of an MCP key check.</summary>
public enum McpAccess
{
    Granted,

    /// <summary>No key or a wrong key was presented.</summary>
    Denied,

    /// <summary><c>MCP__APIKEY</c> is not set, so the endpoint is closed.</summary>
    NotConfigured,
}

/// <summary>Checks the key presented to <c>/mcp</c> in constant time.</summary>
public sealed class McpApiKeyValidator
{
    private readonly IOptionsMonitor<McpOptions> _options;

    public McpApiKeyValidator(IOptionsMonitor<McpOptions> options) => _options = options;

    /// <param name="apiKeyHeader">Value of the <c>X-Api-Key</c> header.</param>
    /// <param name="authorizationHeader">Value of the <c>Authorization</c> header (<c>Bearer &lt;key&gt;</c>).</param>
    public McpAccess Check(string? apiKeyHeader, string? authorizationHeader)
    {
        var expected = _options.CurrentValue.ApiKey;
        if (string.IsNullOrEmpty(expected))
        {
            return McpAccess.NotConfigured;
        }

        var presented = !string.IsNullOrEmpty(apiKeyHeader)
            ? apiKeyHeader
            : authorizationHeader is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth["Bearer ".Length..].Trim()
                : null;

        if (string.IsNullOrEmpty(presented))
        {
            return McpAccess.Denied;
        }

        // Comparing hashes keeps the comparison constant-time regardless of key length.
        var match = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

        return match ? McpAccess.Granted : McpAccess.Denied;
    }
}
