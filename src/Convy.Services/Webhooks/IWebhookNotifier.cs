namespace Convy.Services.Webhooks;

/// <summary>
/// Sends webhook notifications at the end of a sync cycle with a batch
/// of all linked torrents and errors that occurred during the cycle.
/// </summary>
public interface IWebhookNotifier
{
    /// <summary>
    /// Notifies all configured webhooks. Individual webhook failures are logged
    /// but do not propagate; only <see cref="OperationCanceledException"/> is rethrown.
    /// </summary>
    Task NotifyAsync(WebhookBatch batch, CancellationToken cancellationToken);
}

/// <summary>Accumulates torrent results during a sync cycle for a single webhook call.</summary>
public sealed class WebhookBatch
{
    public List<WebhookLinkedItem> Linked { get; } = [];
    public List<WebhookError> Errors { get; } = [];
    public bool HasEntries => Linked.Count > 0 || Errors.Count > 0;

    /// <summary>Records a linked torrent together with the rule that routed it.</summary>
    public void AddLinked(string? ruleName, IReadOnlyDictionary<string, string> properties)
        => Linked.Add(new WebhookLinkedItem(ruleName, properties));
}

/// <summary>
/// A linked torrent in the batch. <see cref="RuleName"/> is the name of the
/// routing rule that matched (<c>null</c> for unnamed rules) and is used to
/// scope which webhooks receive this item.
/// </summary>
public readonly record struct WebhookLinkedItem(
    string? RuleName,
    IReadOnlyDictionary<string, string> Properties);

public readonly record struct WebhookError(string Hash, string Message);
