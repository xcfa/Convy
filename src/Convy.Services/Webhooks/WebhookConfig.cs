namespace Convy.Services.Webhooks;

/// <summary>Defines a single webhook endpoint and how torrent properties map to its parameters.</summary>
public sealed class WebhookConfig
{
    public string? Name { get; set; }
    public string Url { get; set; } = "";

    /// <summary>
    /// Names of the routing rules this webhook fires for. When empty (or null),
    /// the webhook fires for torrents matched by any rule.
    /// </summary>
    public List<string>? Names { get; set; }

    public List<WebhookParam>? Params { get; set; }
}

/// <summary>
/// Maps a torrent property to a named request parameter.
/// <see cref="Value"/> is a case-insensitive torrent property key
/// (hash, name, category, savePath, targetPath, size, state, tags).
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
