using System.Threading.Tasks;
using Convy.Services.Security;
using Microsoft.AspNetCore.Http;

namespace Convy.Middleware;

/// <summary>
/// Rejects requests whose client IP is not in the configured allow-list
/// (see <see cref="IpAccessControlOptions"/>) with <c>403 Forbidden</c>.
/// </summary>
public sealed class IpAccessControlMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IClientIpValidator _validator;

    public IpAccessControlMiddleware(RequestDelegate next, IClientIpValidator validator)
    {
        _next = next;
        _validator = validator;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_validator.IsAllowed(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }
}
