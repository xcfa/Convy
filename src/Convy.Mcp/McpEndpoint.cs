using Convy.Services.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Convy.Mcp;

/// <summary>Registers and maps the MCP endpoint.</summary>
public static class McpEndpoint
{
    /// <summary>Route of the MCP endpoint.</summary>
    public const string Path = "/mcp";

    /// <summary>Adds the MCP server (streamable HTTP, stateless) with Convy's tools.</summary>
    public static IServiceCollection AddConvyMcp(this IServiceCollection services)
    {
        services.AddSingleton<ToolRunner>();
        services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "convy", Version = "1.0" })
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<ConvyMcpTools>();

        return services;
    }

    /// <summary>
    /// Maps <c>/mcp</c> behind the API key check. Call after the IP allow-list middleware,
    /// so both apply.
    /// </summary>
    public static WebApplication MapConvyMcp(this WebApplication app)
    {
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(Path),
            branch => branch.UseMiddleware<McpApiKeyMiddleware>());

        app.MapMcp(Path);
        return app;
    }
}

/// <summary>Rejects <c>/mcp</c> requests without the right key (<c>X-Api-Key</c> or <c>Authorization: Bearer</c>).</summary>
public sealed class McpApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly McpApiKeyValidator _validator;

    public McpApiKeyMiddleware(RequestDelegate next, McpApiKeyValidator validator)
    {
        _next = next;
        _validator = validator;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        switch (_validator.Check(context.Request.Headers["X-Api-Key"], context.Request.Headers.Authorization))
        {
            case McpAccess.Granted:
                await _next(context).ConfigureAwait(false);
                return;

            case McpAccess.NotConfigured:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("MCP is disabled: set MCP__APIKEY.", context.RequestAborted).ConfigureAwait(false);
                return;

            default:
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
        }
    }
}
