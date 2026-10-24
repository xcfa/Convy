using Convy.Services.External;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Convy.Controllers.External;

/// <summary>Turns an <see cref="ApiException"/> of the external API into a problem response with its status.</summary>
public sealed class ApiErrorFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ApiException error)
        {
            return;
        }

        context.Result = new ObjectResult(new ProblemDetails { Status = error.Status, Title = error.Message })
        {
            StatusCode = error.Status,
        };
        context.ExceptionHandled = true;
    }
}
