using System.Text.Json.Serialization;
using Convy.Services.Jobs;
using Convy.Services.Rules;

namespace Convy.Services.Webhooks;

/// <summary>A webhook as the web UI shows it.</summary>
/// <param name="Id">Id of a webhook created in the UI; <c>null</c> for one from configuration.yml.</param>
/// <param name="FileIndex">Position of a webhook from configuration.yml among them; <c>null</c> for one from the UI.</param>
/// <param name="Source"><c>ui</c> (editable) or <c>file</c> (configuration.yml, read-only).</param>
/// <param name="Headers">The headers, without their values: those are never sent to the browser.</param>
public sealed record WebhookDto(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("file_index")] int? FileIndex,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("events")] IReadOnlyList<string> Events,
    [property: JsonPropertyName("names")] IReadOnlyList<string> Names,
    [property: JsonPropertyName("params")] IReadOnlyList<WebhookParamDto> Params,
    [property: JsonPropertyName("headers")] IReadOnlyList<WebhookHeaderDto> Headers,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record WebhookParamDto(
    [property: JsonPropertyName("place")] string Place,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

/// <summary>
/// A header. The UI always gets it without a value; a header sent back without a value keeps
/// the value the webhook already has for that name.
/// </summary>
public sealed record WebhookHeaderDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string? Value);

/// <summary>Everything the webhook page needs.</summary>
/// <param name="Fields">Per event, the fields a parameter can pick.</param>
/// <param name="Rules">Names of the routing rules, for the <c>names</c> filter.</param>
public sealed record WebhookListResponse(
    [property: JsonPropertyName("webhooks")] IReadOnlyList<WebhookDto> Webhooks,
    [property: JsonPropertyName("events")] IReadOnlyList<string> Events,
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, IReadOnlyList<string>> Fields,
    [property: JsonPropertyName("rules")] IReadOnlyList<string> Rules);

/// <summary>A webhook as the UI sends it for saving or testing.</summary>
public sealed class WebhookInput
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("events")] public List<string>? Events { get; init; }
    [JsonPropertyName("names")] public List<string>? Names { get; init; }
    [JsonPropertyName("params")] public List<WebhookParamDto>? Params { get; init; }
    [JsonPropertyName("headers")] public List<WebhookHeaderDto>? Headers { get; init; }
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
}

/// <summary>
/// A test request: a webhook (saved or not) and the event to send. <c>id</c> or
/// <c>file_index</c> names the webhook it was opened from, whose header values fill in those
/// the browser does not have.
/// </summary>
public sealed class WebhookTestRequest
{
    [JsonPropertyName("webhook")] public WebhookInput? Webhook { get; init; }
    [JsonPropertyName("event")] public string? Event { get; init; }
    [JsonPropertyName("id")] public int? Id { get; init; }
    [JsonPropertyName("file_index")] public int? FileIndex { get; init; }
}

/// <summary>What happened when a test event was sent.</summary>
public sealed record WebhookTestResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("status_code")] int? StatusCode,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("request_body")] string RequestBody,
    [property: JsonPropertyName("response_body")] string? ResponseBody,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>
/// Lists, saves and tests webhooks for the web UI. Webhooks from configuration.yml are shown
/// and can be tested but not changed; those created in the UI are stored in the database.
/// </summary>
public sealed class WebhookAdminService
{
    /// <summary>Events in the order the UI offers them.</summary>
    public static readonly IReadOnlyList<string> EventNames = [WebhookEvents.Linked, WebhookEvents.JobStatus, WebhookEvents.SourceError];

    /// <summary>Fields a parameter can pick, per event.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EventFields = new Dictionary<string, IReadOnlyList<string>>
    {
        [WebhookEvents.Linked] = ["hash", "name", "category", "savePath", "targetPath", "size", "state", "tags", "job_id", "provider"],
        [WebhookEvents.JobStatus] =
        [
            "event", "job_id", "status", "previous_status", "provider", "category", "rule", "title", "path",
            "files", "files_total", "size_bytes", "error", "releases",
        ],
        [WebhookEvents.SourceError] = ["event", "source", "status", "message"],
    };

    private readonly WebhookCatalog _catalog;
    private readonly IWebhookStore _store;
    private readonly WebhookSender _sender;
    private readonly IRulesProvider _rules;

    public WebhookAdminService(WebhookCatalog catalog, IWebhookStore store, WebhookSender sender, IRulesProvider rules)
    {
        _catalog = catalog;
        _store = store;
        _sender = sender;
        _rules = rules;
    }

    public WebhookListResponse List()
    {
        var webhooks = _catalog.FileWebhooks.Select((w, i) => ToDto(null, i, "file", w, enabled: true))
            .Concat(_catalog.StoredWebhooks.Select(w => ToDto(w.Id, null, "ui", w.Config, w.Enabled)))
            .ToList();

        var rules = _rules.GetCurrent().Mappings.Rules
            .Select(r => r.Name)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new WebhookListResponse(webhooks, EventNames, EventFields, rules);
    }

    public async Task<WebhookDto> CreateAsync(WebhookInput input, CancellationToken cancellationToken)
    {
        var config = Validate(input);
        var stored = await _store.AddAsync(config, input.Enabled, cancellationToken).ConfigureAwait(false);
        await _catalog.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(stored.Id, null, "ui", stored.Config, stored.Enabled);
    }

    public async Task<WebhookDto> UpdateAsync(int id, WebhookInput input, CancellationToken cancellationToken)
    {
        var config = Validate(input, _catalog.StoredWebhooks.FirstOrDefault(w => w.Id == id)?.Config);
        var stored = await _store.UpdateAsync(id, config, input.Enabled, cancellationToken).ConfigureAwait(false)
                     ?? throw new ConvyRequestException($"Webhook {id} does not exist.");
        await _catalog.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(stored.Id, null, "ui", stored.Config, stored.Enabled);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        if (!await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false))
        {
            throw new ConvyRequestException($"Webhook {id} does not exist.");
        }

        await _catalog.ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a sample event to the webhook as it is in the editor (saved or not), with the
    /// webhook's parameters applied and an <c>X-Convy-Test: true</c> header. The event and
    /// rule filters are ignored: the test is sent whatever the webhook subscribes to.
    /// </summary>
    public async Task<WebhookTestResult> TestAsync(WebhookTestRequest request, CancellationToken cancellationToken)
    {
        var opened = request.Id is { } id
            ? _catalog.StoredWebhooks.FirstOrDefault(w => w.Id == id)?.Config
            : request.FileIndex is { } index && index >= 0 && index < _catalog.FileWebhooks.Count
                ? _catalog.FileWebhooks[index]
                : null;
        var config = Validate(request.Webhook ?? new WebhookInput(), opened);
        var eventName = request.Event ?? WebhookEvents.Linked;

        var (body, query) = eventName switch
        {
            WebhookEvents.Linked => WebhookNotifier.BuildLinked(config, [SampleLinkedItem()], []),
            WebhookEvents.JobStatus => WebhookSender.Project(config, WebhookJobEvents.ToEvent(SampleJobChange()).Payload),
            WebhookEvents.SourceError => WebhookSender.Project(config, SampleSourceError()),
            _ => throw new ConvyRequestException($"Unknown event '{eventName}'. Use one of: {string.Join(", ", EventNames)}."),
        };

        var delivery = await _sender.DeliverAsync(config, body, query, test: true, cancellationToken).ConfigureAwait(false);
        return new WebhookTestResult(
            delivery.Success,
            delivery.StatusCode,
            (long)delivery.Duration.TotalMilliseconds,
            delivery.Url,
            delivery.RequestBody,
            delivery.ResponseBody,
            delivery.Exception is null ? null : Describe(delivery.Exception));
    }

    /// <summary>
    /// Checks a webhook from the UI and turns it into a configuration; throws with every problem
    /// found. A header without a value takes the value <paramref name="existing"/> has for it.
    /// </summary>
    public static WebhookConfig Validate(WebhookInput input, WebhookConfig? existing = null)
    {
        var problems = new List<string>();

        var url = input.Url?.Trim();
        if (string.IsNullOrEmpty(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            problems.Add("url must be an absolute http(s) address.");
        }

        var name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim();
        if (name?.Length > 256)
        {
            problems.Add("name must be at most 256 characters.");
        }

        var events = (input.Events ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        foreach (var unknown in events.Where(e => !EventNames.Contains(e)))
        {
            problems.Add($"Unknown event '{unknown}'. Use {string.Join(", ", EventNames)}.");
        }

        var names = (input.Names ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        var parameters = new List<WebhookParam>();
        foreach (var (param, index) in (input.Params ?? []).Select((p, i) => (p, i + 1)))
        {
            if (string.IsNullOrWhiteSpace(param.Name) || string.IsNullOrWhiteSpace(param.Value))
            {
                problems.Add($"Parameter {index} needs a name and a field.");
                continue;
            }

            if (!Enum.TryParse<WebhookParamPlace>(param.Place, ignoreCase: true, out var place)
                || !Enum.IsDefined(place))
            {
                problems.Add($"Parameter {index}: place must be query or body.");
                continue;
            }

            parameters.Add(new WebhookParam { Place = place, Name = param.Name.Trim(), Value = param.Value.Trim() });
        }

        var headers = new List<WebhookHeader>();
        foreach (var (header, index) in (input.Headers ?? []).Select((h, i) => (h, i + 1)))
        {
            var headerName = header.Name?.Trim() ?? string.Empty;
            if (headerName.Length == 0)
            {
                problems.Add($"Header {index} needs a name.");
                continue;
            }

            if (headers.Any(h => string.Equals(h.Name, headerName, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"Header {headerName} is given twice.");
                continue;
            }

            var value = string.IsNullOrEmpty(header.Value)
                ? existing?.Headers?.FirstOrDefault(h => string.Equals(h.Name, headerName, StringComparison.OrdinalIgnoreCase))?.Value
                : header.Value.Trim();
            if (string.IsNullOrEmpty(value))
            {
                problems.Add($"Header {headerName} needs a value.");
                continue;
            }

            if (WebhookHeader.Problem(headerName, value) is { } problem)
            {
                problems.Add(problem);
                continue;
            }

            headers.Add(new WebhookHeader { Name = headerName, Value = value });
        }

        if (problems.Count > 0)
        {
            throw new ConvyRequestException(string.Join(" ", problems));
        }

        return new WebhookConfig
        {
            Name = name,
            Url = url!,
            Events = events.Count > 0 ? events : null,
            Names = names.Count > 0 ? names : null,
            Params = parameters.Count > 0 ? parameters : null,
            Headers = headers.Count > 0 ? headers : null,
        };
    }

    private static WebhookDto ToDto(int? id, int? fileIndex, string source, WebhookConfig config, bool enabled) => new(
        id,
        fileIndex,
        source,
        config.Name,
        config.Url,
        config.Events is { Count: > 0 } events ? events : [WebhookEvents.Linked],
        config.Names ?? [],
        (config.Params ?? []).Select(p => new WebhookParamDto(p.Place.ToString().ToLowerInvariant(), p.Name, p.Value)).ToList(),
        (config.Headers ?? []).Select(h => new WebhookHeaderDto(h.Name, null)).ToList(),
        enabled);

    private static string Describe(Exception exception) =>
        exception is TaskCanceledException ? "No answer in time." : exception.GetBaseException().Message;

    private static WebhookLinkedItem SampleLinkedItem() => new("test", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["hash"] = "0123456789abcdef0123456789abcdef01234567",
        ["name"] = "Convy test - Evanescence - Fallen",
        ["category"] = "Music",
        ["savePath"] = "/data/downloads",
        ["targetPath"] = "/data/media/music/Evanescence/2003 - Fallen",
        ["size"] = "432000000",
        ["state"] = "stalledUP",
        ["tags"] = "test",
        ["job_id"] = "j_0",
        ["provider"] = "qbittorrent",
    });

    private static JobStatusChange SampleJobChange()
    {
        var release = new JobRecord
        {
            Id = 0,
            Provider = "qbittorrent",
            ItemRef = "test",
            Category = "music",
            ClientCategory = "Music",
            Title = "Convy test - Evanescence - Fallen",
            Status = JobStatus.Completed,
            Rule = "test",
            TargetPath = "/data/media/music/Evanescence/2003 - Fallen",
            SizeBytes = 432000000,
            FileCount = 2,
            PlacedFiles = ["01 - Going Under.mp3", "02 - Bring Me to Life.mp3"],
        };

        return new JobStatusChange(JobState.From([release]), JobStatus.Placing);
    }

    private static Dictionary<string, object?> SampleSourceError() => new()
    {
        ["event"] = WebhookEvents.SourceError,
        ["source"] = "prowlarr:1",
        ["status"] = "auth_failed",
        ["message"] = "Convy test: the indexer rejected the API key.",
    };
}
