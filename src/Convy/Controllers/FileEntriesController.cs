using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Files;
using Microsoft.AspNetCore.Mvc;

namespace Convy.Controllers;

[ApiController]
[Route("api/v1/files")]
public class FileEntriesController : ControllerBase
{
    private readonly IFileEntryQueryService _service;

    public FileEntriesController(IFileEntryQueryService service)
    {
        _service = service;
    }

    /// <summary>
    /// Returns linked file entries with optional filtering.
    /// All filter parameters are combined with AND.
    /// </summary>
    [HttpGet]
    public Task<List<FileEntryDto>> GetAll(
        [FromQuery] FileEntryFilter filter, CancellationToken cancellationToken)
        => _service.QueryAsync(filter, cancellationToken);
}
