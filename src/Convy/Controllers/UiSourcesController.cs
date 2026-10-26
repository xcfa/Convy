using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services;
using Convy.Services.Extensions;
using Convy.Ui;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Convy.Controllers;

/// <summary>The Sources page: extensions, their settings and the indexes they come from. Signed-in users only.</summary>
[ApiController]
[UiController]
[Route("api/ui/sources")]
[Authorize(Policy = UiSetup.Policy)]
[ApiExplorerSettings(IgnoreApi = true)]
[ServiceFilter(typeof(UiRequestErrorFilter))]
public sealed class UiSourcesController : ControllerBase
{
    /// <summary>The largest package that can be uploaded.</summary>
    private const long MaxPackageBytes = 32L * 1024 * 1024;

    private readonly ExtensionAdminService _extensions;

    public UiSourcesController(ExtensionAdminService extensions) => _extensions = extensions;

    /// <summary>The extensions installed, the packages that failed, the indexes.</summary>
    [HttpGet]
    public SourcesView Get() => _extensions.Get();

    /// <summary>The packages of the indexes that are for Convy; <paramref name="fresh"/> — not from the cache.</summary>
    [HttpGet("available")]
    public Task<AvailableView> Available(bool fresh = false, CancellationToken cancellationToken = default)
        => _extensions.AvailableAsync(fresh, cancellationToken);

    /// <summary>Installs or updates a package from an index.</summary>
    [HttpPost("install")]
    public Task<InstalledExtensionDto> Install([FromBody] InstallInput input, CancellationToken cancellationToken)
        => _extensions.InstallAsync(input.Index, input.Package, cancellationToken);

    /// <summary>Installs an uploaded package (a .zip or a .dll).</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(MaxPackageBytes + 64 * 1024)]
    public async Task<InstalledExtensionDto> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > MaxPackageBytes)
        {
            throw new ConvyRequestException($"A package is at most {MaxPackageBytes / (1024 * 1024)} MB.");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        return await _extensions.UploadAsync(buffer.ToArray(), cancellationToken);
    }

    /// <summary>Removes an installed package.</summary>
    [HttpDelete("packages/{package}")]
    public Task Remove(string package, CancellationToken cancellationToken) => _extensions.RemoveAsync(package, cancellationToken);

    /// <summary>Deletes a package file that could not be loaded.</summary>
    [HttpDelete("problems/{file}")]
    public Task RemoveBroken(string file, CancellationToken cancellationToken) => _extensions.RemoveBrokenAsync(file, cancellationToken);

    /// <summary>Saves what a source or a provider is set to.</summary>
    [HttpPut("settings/{id}")]
    public Task SaveSettings(string id, [FromBody] SourceSettingsInput input, CancellationToken cancellationToken)
        => _extensions.SaveSettingsAsync(id, input, cancellationToken);

    /// <summary>Signs a site in and saves its cookies.</summary>
    [HttpPost("login/{id}")]
    public Task Login(string id, [FromBody] LoginInput input, CancellationToken cancellationToken)
        => _extensions.LoginAsync(id, input.Fields, cancellationToken);

    /// <summary>Adds an index.</summary>
    [HttpPost("indexes")]
    public Task AddIndex([FromBody] IndexInput input, CancellationToken cancellationToken) => _extensions.AddIndexAsync(input, cancellationToken);

    /// <summary>Removes an index added on the page.</summary>
    [HttpDelete("indexes")]
    public Task RemoveIndex(string url, CancellationToken cancellationToken) => _extensions.RemoveIndexAsync(url, cancellationToken);
}
