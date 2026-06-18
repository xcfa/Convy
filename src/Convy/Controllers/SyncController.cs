using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Sync;
using Microsoft.AspNetCore.Mvc;

namespace Convy.Controllers;

[ApiController]
[Route("api/v1/sync")]
public class SyncController : ControllerBase
{
    private readonly ISyncControlService _service;

    public SyncController(ISyncControlService service)
    {
        _service = service;
    }

    /// <summary>Returns the current sync state.</summary>
    [HttpGet]
    public SyncStatusDto GetStatus() => _service.GetStatus();

    /// <summary>Updates sync settings. Only provided fields are changed.</summary>
    [HttpPatch]
    public Task<SyncStatusDto> UpdateSettings(
        [FromBody] SyncSettingsUpdateDto dto, CancellationToken cancellationToken)
        => _service.UpdateSettingsAsync(dto, cancellationToken);

    /// <summary>Triggers a one-off sync cycle regardless of auto-sync state.</summary>
    [HttpPost("trigger")]
    public IActionResult TriggerSync()
        => _service.TryTriggerSync()
            ? Accepted(new { message = "Sync triggered." })
            : Conflict(new { message = "A sync cycle is already running." });
}
