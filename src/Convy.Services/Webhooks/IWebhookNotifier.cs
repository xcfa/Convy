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
    public List<IReadOnlyDictionary<string, string>> Linked { get; } = [];
    public List<WebhookError> Errors { get; } = [];
    public bool HasEntries => Linked.Count > 0 || Errors.Count > 0;
}

public readonly record struct WebhookError(string Hash, string Message);
