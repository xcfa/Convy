using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Convy.Ui;

/// <summary>Marks a controller that belongs to the web UI.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class UiControllerAttribute : Attribute
{
}

/// <summary>Removes the UI's controllers when the UI is off, so their routes do not exist.</summary>
public sealed class UiControllersConvention : IApplicationModelConvention
{
    private readonly bool _enabled;

    public UiControllersConvention(bool enabled) => _enabled = enabled;

    public void Apply(ApplicationModel application)
    {
        if (_enabled)
        {
            return;
        }

        foreach (var controller in application.Controllers
                     .Where(c => c.ControllerType.IsDefined(typeof(UiControllerAttribute), inherit: false))
                     .ToList())
        {
            application.Controllers.Remove(controller);
        }
    }
}
