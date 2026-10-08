using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Diagnostics;
using Convy.Services.Media;
using Convy.Services.Sync;
using Convy.Services.Ui;
using Convy.Ui;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Convy.Controllers;

/// <summary>The web UI's API. Signed-in users only (see <see cref="UiSetup"/>).</summary>
[ApiController]
[UiController]
[Route("api/ui")]
[Authorize(Policy = UiSetup.Policy)]
[ApiExplorerSettings(IgnoreApi = true)]
[ServiceFilter(typeof(UiRequestErrorFilter))]
public sealed class UiController : ControllerBase
{
    private readonly UiAuthService _auth;
    private readonly UiStatusService _status;
    private readonly LogBuffer _logs;
    private readonly MediaCatalogService _catalog;
    private readonly ISyncControlService _sync;
    private readonly DataBrowserService _data;

    public UiController(
        UiAuthService auth,
        UiStatusService status,
        LogBuffer logs,
        MediaCatalogService catalog,
        ISyncControlService sync,
        DataBrowserService data)
    {
        _auth = auth;
        _status = status;
        _logs = logs;
        _catalog = catalog;
        _sync = sync;
        _data = data;
    }

    /// <summary>The signed-in user.</summary>
    [HttpGet("me")]
    public UiUser Me() => _auth.Describe(User);

    /// <summary>Sync, downloaders, sources, storage check and job counts.</summary>
    [HttpGet("status")]
    public Task<UiStatus> Status(CancellationToken cancellationToken) => _status.GetAsync(cancellationToken);

    /// <summary>Recent log entries newer than <paramref name="after"/>.</summary>
    [HttpGet("logs")]
    public LogPage Logs(long after = 0, LogSeverity level = LogSeverity.Information, string? q = null, int limit = 500)
        => _logs.Read(after, level, q, limit);

    /// <summary>Jobs, newest first.</summary>
    [HttpGet("jobs")]
    public Task<JobsResponse> Jobs(string? status, int? limit, CancellationToken cancellationToken)
        => _catalog.GetJobsAsync(status, limit, cancellationToken);

    /// <summary>Stops a job's download.</summary>
    [HttpPost("jobs/{jobId}/cancel")]
    public Task<CancelJobResponse> CancelJob(string jobId, CancellationToken cancellationToken)
        => _catalog.CancelJobAsync(jobId, cancellationToken);

    /// <summary>Queues a sync cycle now.</summary>
    [HttpPost("sync")]
    public IActionResult Sync()
    {
        _sync.QueueSync();
        return Accepted();
    }

    /// <summary>The tables that can be browsed.</summary>
    [HttpGet("data")]
    public Task<IReadOnlyList<DataTableInfo>> Tables(CancellationToken cancellationToken) => _data.GetTablesAsync(cancellationToken);

    /// <summary>One page of a table.</summary>
    [HttpGet("data/{table}")]
    public Task<DataPage> Table(
        string table, string? q, string? sort, bool desc = true, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
        => _data.ReadAsync(table, new DataQuery(q, sort, desc, offset, limit), cancellationToken);
}
