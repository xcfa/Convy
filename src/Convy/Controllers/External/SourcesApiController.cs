using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services.External;
using Microsoft.AspNetCore.Mvc;

namespace Convy.Controllers.External;

/// <summary>
/// The gallery sources for external clients (the Shizuka provider), behind the API key: the sources with their
/// manifests, a search and a link lookup through a source.
/// </summary>
[ApiController]
[Route("api/sources")]
[TypeFilter(typeof(ApiErrorFilter))]
public sealed class SourcesApiController : ControllerBase
{
    private static readonly HashSet<string> Reserved = new(System.StringComparer.OrdinalIgnoreCase) { "q", "language", "page" };

    private readonly GalleryApiService _api;

    public SourcesApiController(GalleryApiService api) => _api = api;

    /// <summary>Every gallery source with what it can do.</summary>
    [HttpGet]
    public Task<IReadOnlyList<ApiSourceDto>> List(CancellationToken cancellationToken) => _api.ListSourcesAsync(cancellationToken);

    /// <summary>
    /// A page of releases: <c>q</c> (the query; empty for the site's front page), <c>language</c>, <c>page</c> (the
    /// token of the previous answer) and any other parameter as a filter (<c>category=manga&amp;category=doujinshi</c>).
    /// </summary>
    [HttpGet("{id}/search")]
    public Task<ApiSearchDto> Search(string id, [FromQuery] string? q, [FromQuery] string? language, [FromQuery] string? page, CancellationToken cancellationToken)
    {
        var filters = Request.Query
            .Where(p => !Reserved.Contains(p.Key))
            .ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList(), System.StringComparer.OrdinalIgnoreCase);
        return _api.SearchAsync(id, q, language, filters, page, cancellationToken);
    }

    /// <summary>The release a link of the site points to; 404 when the source has none.</summary>
    [HttpGet("{id}/resolve")]
    public Task<ApiContentDto> Resolve(string id, [FromQuery] string url, CancellationToken cancellationToken) =>
        _api.ResolveAsync(id, url ?? string.Empty, cancellationToken);
}
