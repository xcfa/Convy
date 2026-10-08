using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Convy.Ui;

/// <summary>
/// Serves the built UI (<c>wwwroot</c>, produced by <c>npm run build</c> in <c>frontend/</c>).
/// Files are served through endpoints rather than the static-files middleware so they sit
/// behind the UI's sign-in like the API.
/// </summary>
public sealed class UiAssets
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly IFileProvider _files;

    public UiAssets(IWebHostEnvironment environment)
    {
        var root = Path.Combine(environment.ContentRootPath, "wwwroot");
        _files = Directory.Exists(root) ? new PhysicalFileProvider(root) : new NullFileProvider();
    }

    public IResult Index()
    {
        var file = _files.GetFileInfo("index.html");
        if (!file.Exists || file.PhysicalPath is null)
        {
            return Results.Text("The UI is not built. Run `npm ci && npm run build` in frontend/.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return new CachedFileResult(file.PhysicalPath, "text/html; charset=utf-8", "no-cache");
    }

    /// <summary>A file under <c>assets/</c>. Vite puts a content hash into every name, so they never change.</summary>
    public IResult Asset(string path)
    {
        // PhysicalFileProvider refuses paths that leave its root.
        var file = _files.GetFileInfo("assets/" + path);
        if (!file.Exists || file.IsDirectory || file.PhysicalPath is null)
        {
            return Results.NotFound();
        }

        var contentType = ContentTypes.TryGetContentType(file.Name, out var type) ? type : "application/octet-stream";
        return new CachedFileResult(file.PhysicalPath, contentType, "private, max-age=31536000, immutable");
    }

    private sealed class CachedFileResult : IResult
    {
        private readonly string _path;
        private readonly string _contentType;
        private readonly string _cacheControl;

        public CachedFileResult(string path, string contentType, string cacheControl)
        {
            _path = path;
            _contentType = contentType;
            _cacheControl = cacheControl;
        }

        public System.Threading.Tasks.Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = _cacheControl;
            httpContext.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(_path, _contentType).ExecuteAsync(httpContext);
        }
    }
}
