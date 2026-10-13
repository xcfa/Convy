using System.Text.RegularExpressions;

namespace Convy.Services.Webhooks;

/// <summary>Defines a single webhook endpoint and how properties map to its parameters.</summary>
public sealed class WebhookConfig
{
    public string? Name { get; set; }
    public string Url { get; set; } = "";

    /// <summary>
    /// Names of the routing rules this webhook fires for. When empty (or null),
    /// the webhook fires for items matched by any rule. Applies to <c>linked</c> items and
    /// to <c>job_status</c> events (by the job's rule).
    /// </summary>
    public List<string>? Names { get; set; }

    /// <summary>
    /// Events this webhook receives: <c>linked</c>, <c>job_status</c>, <c>source_error</c>.
    /// When empty (or null) it receives only <c>linked</c>, as before events existed.
    /// </summary>
    public List<string>? Events { get; set; }

    public List<WebhookParam>? Params { get; set; }

    /// <summary>
    /// Headers sent with every request, e.g. <c>Authorization</c> for a receiver that wants a
    /// token. Their values are treated as secrets: never shown in the web UI or the logs.
    /// </summary>
    public List<WebhookHeader>? Headers { get; set; }
}

/// <summary>A header sent with every request of a webhook.</summary>
public sealed partial class WebhookHeader
{
    /// <summary>Longest header value accepted.</summary>
    public const int MaxValueLength = 8192;

    // Set by Convy or by HTTP itself; a webhook cannot override them.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Content-Encoding", "Transfer-Encoding", "Connection", "Keep-Alive",
        "Proxy-Connection", "TE", "Trailer", "Upgrade", "Expect", "X-Convy-Test",
    };

    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>Why a header cannot be sent (without its value), or <c>null</c> when it can.</summary>
    public static string? Problem(string name, string value)
    {
        if (!TokenPattern().IsMatch(name))
        {
            return $"'{name}' is not a header name (letters, digits and !#$%&'*+-.^_`|~ only).";
        }

        if (Reserved.Contains(name))
        {
            return $"{name} is set by Convy and cannot be changed.";
        }

        if (value.Length > MaxValueLength)
        {
            return $"The value of {name} is longer than {MaxValueLength} characters.";
        }

        return value.Any(c => char.IsControl(c) && c != '\t')
            ? $"The value of {name} contains a line break or another control character."
            : null;
    }

    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]{1,256}$")]
    private static partial Regex TokenPattern();
}

/// <summary>
/// Maps a property to a named request parameter.
/// <see cref="Value"/> is a case-insensitive property key: for <c>linked</c> items one of
/// hash, name, category, savePath, targetPath, size, state, tags, job_id, provider; for
/// events a field of the event body (job_id, status, path, …).
/// </summary>
public sealed class WebhookParam
{
    public WebhookParamPlace Place { get; set; } = WebhookParamPlace.Query;
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public enum WebhookParamPlace
{
    Query,
    Body
}

/// <summary>Webhook event names and subscription rules.</summary>
public static class WebhookEvents
{
    /// <summary>Once per sync cycle when something was placed or failed (batch).</summary>
    public const string Linked = "linked";

    /// <summary>On every job status change (one request per change).</summary>
    public const string JobStatus = "job_status";

    /// <summary>When a source turns <c>auth_failed</c> or <c>error</c>.</summary>
    public const string SourceError = "source_error";

    /// <summary>Whether <paramref name="webhook"/> receives events named <paramref name="eventName"/>.</summary>
    public static bool Subscribes(WebhookConfig webhook, string eventName) =>
        webhook.Events is { Count: > 0 } events
            ? events.Contains(eventName, StringComparer.OrdinalIgnoreCase)
            : eventName == Linked;
}
