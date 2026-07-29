using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Convy.Health;

/// <summary>
/// Writes the health report as a compact JSON object:
/// <c>{ "status": "...", "checks": [ { "name", "status", "description" } ] }</c>.
/// </summary>
public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
            }),
        };

        return context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, Options), context.RequestAborted);
    }
}
