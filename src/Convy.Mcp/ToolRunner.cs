using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Convy.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Convy.Mcp;

/// <summary>
/// Runs a tool's service call and builds the tool result: compact JSON on success, an error
/// result the agent can read when the request is rejected. Unexpected failures are logged
/// and reported without internal details.
/// </summary>
public sealed class ToolRunner
{
    // Compact: no nulls, no indentation, non-ASCII text (Cyrillic titles) kept readable.
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger<ToolRunner> _logger;

    public ToolRunner(ILogger<ToolRunner> logger) => _logger = logger;

    public async Task<CallToolResult> Run<T>(string tool, Func<Task<T>> call)
    {
        try
        {
            var value = await call().ConfigureAwait(false);
            return Result(JsonSerializer.Serialize(value, Json), isError: false);
        }
        catch (ConvyRequestException ex)
        {
            _logger.LogInformation("MCP tool {Tool} rejected the request: {Reason}", tool, ex.Message);
            return Result(ex.Message, isError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "MCP tool {Tool} failed.", tool);
            return Result($"{tool} failed because of an internal error; see the Convy logs.", isError: true);
        }
    }

    private static CallToolResult Result(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
