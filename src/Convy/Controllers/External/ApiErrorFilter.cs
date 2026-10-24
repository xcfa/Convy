using Convy.Services.External;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Convy.Controllers.External;

/// <summary>Turns an <see cref="ApiException"/> of the external API into <c>{ "error": "…" }</c> with its status (the README's contract).</summary>
public sealed class ApiErrorFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ApiException error)
        {
            return;
        }

        context.Result = new ObjectResult(new ApiErrorDto(error.Message, error.ChallengeUrl?.AbsoluteUri)) { StatusCode = error.Status };
        context.ExceptionHandled = true;
    }
}
