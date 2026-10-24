using System.Threading;
using System.Threading.Tasks;
using Convy.Services.External;
using Microsoft.AspNetCore.Mvc;

namespace Convy.Controllers.External;

/// <summary>
/// Gallery jobs for external clients, behind the API key: start one for a release, follow it, fetch the CBZ. The
/// job goes through Convy's rules like any other, so the archive may also land in the media library.
/// </summary>
[ApiController]
[Route("api/jobs")]
[TypeFilter(typeof(ApiErrorFilter))]
public sealed class JobsApiController : ControllerBase
{
    private readonly GalleryApiService _api;

    public JobsApiController(GalleryApiService api) => _api = api;

    /// <summary>Starts a job for a release of a gallery source (or returns the one already running for it).</summary>
    [HttpPost]
    public Task<ApiJobDto> Start([FromBody] ApiStartJobRequest request, CancellationToken cancellationToken) =>
        _api.StartJobAsync(request, cancellationToken);

    /// <summary>The job's status and the gallery's progress.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<ApiJobDto>> Get(string id, CancellationToken cancellationToken) =>
        await _api.GetJobAsync(id, cancellationToken) is { } job ? job : NotFound(new ProblemDetails { Status = 404, Title = $"Job {id} does not exist." });

    /// <summary>The finished CBZ; 404 until the download is complete.</summary>
    [HttpGet("{id}/archive")]
    public async Task<IActionResult> Archive(string id, CancellationToken cancellationToken)
    {
        var (path, fileName) = await _api.GetArchiveAsync(id, cancellationToken);
        return PhysicalFile(path, "application/vnd.comicbook+zip", fileName, enableRangeProcessing: true);
    }
}
