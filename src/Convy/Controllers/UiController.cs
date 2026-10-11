using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Diagnostics;
using Convy.Services.Media;
using Convy.Services.Sync;
using Convy.Services.Ui;
using Convy.Services.Webhooks;
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
    private readonly WebhookAdminService _webhooks;
    private readonly RulesViewService _rules;

    public UiController(
        UiAuthService auth,
        UiStatusService status,
        LogBuffer logs,
        MediaCatalogService catalog,
        ISyncControlService sync,
        DataBrowserService data,
        WebhookAdminService webhooks,
        RulesViewService rules)
    {
        _auth = auth;
        _status = status;
        _logs = logs;
        _catalog = catalog;
        _sync = sync;
        _data = data;
        _webhooks = webhooks;
        _rules = rules;
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

    /// <summary>Webhooks from configuration.yml (read-only) and from the UI, with the events, fields and rules to choose from.</summary>
    [HttpGet("webhooks")]
    public WebhookListResponse Webhooks() => _webhooks.List();

    /// <summary>Creates a webhook.</summary>
    [HttpPost("webhooks")]
    public Task<WebhookDto> CreateWebhook([FromBody] WebhookInput input, CancellationToken cancellationToken)
        => _webhooks.CreateAsync(input, cancellationToken);

    /// <summary>Replaces a webhook created in the UI.</summary>
    [HttpPut("webhooks/{id:int}")]
    public Task<WebhookDto> UpdateWebhook(int id, [FromBody] WebhookInput input, CancellationToken cancellationToken)
        => _webhooks.UpdateAsync(id, input, cancellationToken);

    /// <summary>Deletes a webhook created in the UI.</summary>
    [HttpDelete("webhooks/{id:int}")]
    public Task DeleteWebhook(int id, CancellationToken cancellationToken) => _webhooks.DeleteAsync(id, cancellationToken);

    /// <summary>Sends a sample event to a webhook as it is in the editor and reports the answer.</summary>
    [HttpPost("webhooks/test")]
    public Task<WebhookTestResult> TestWebhook([FromBody] WebhookTestRequest request, CancellationToken cancellationToken)
        => _webhooks.TestAsync(request, cancellationToken);

    /// <summary>The rules file and the rules in effect.</summary>
    [HttpGet("rules")]
    public Task<RulesView> Rules(CancellationToken cancellationToken) => _rules.GetAsync(cancellationToken);
}
