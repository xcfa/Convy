using System.Collections.Generic;
using Convy.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Convy.Ui;

/// <summary>Turns a refused request (<see cref="ConvyRequestException"/>) into 400 with its message.</summary>
public sealed class UiRequestErrorFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is ConvyRequestException refused)
        {
            context.Result = new BadRequestObjectResult(new Dictionary<string, string> { ["error"] = refused.Message });
            context.ExceptionHandled = true;
        }
    }
}
