using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Webhooks;

/// <summary>A single-request webhook event (<c>job_status</c>, <c>source_error</c>).</summary>
/// <param name="Name">Event name, see <see cref="WebhookEvents"/>.</param>
/// <param name="RuleName">Rule the event relates to; matched against <see cref="WebhookConfig.Names"/>, if any.</param>
/// <param name="Payload">The JSON body (field name → value).</param>
public sealed record WebhookEvent(string Name, string? RuleName, IReadOnlyDictionary<string, object?> Payload);

/// <summary>Accepts webhook events for delivery; never blocks and never throws.</summary>
public interface IWebhookEventQueue
{
    void Enqueue(WebhookEvent webhookEvent);
}

/// <summary>Retry settings of event delivery.</summary>
public sealed class WebhookDeliveryOptions
{
    /// <summary>Retries after a failed request.</summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>Delay before the first retry; doubles with every retry.</summary>
    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Delivers webhook events in order through a bounded channel, one POST per event and
/// subscribed webhook, retrying failures with a growing delay. Producers (job status
/// changes, source health) only enqueue; <see cref="RunAsync"/> runs in a hosted service.
/// </summary>
public sealed class WebhookEventDispatcher : IWebhookEventQueue
{
    private const int Capacity = 1000;

    private readonly Func<IReadOnlyList<WebhookConfig>> _webhooks;
    private readonly WebhookSender _sender;
    private readonly WebhookDeliveryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookEventDispatcher> _logger;
    private readonly Channel<WebhookEvent> _channel;

    public WebhookEventDispatcher(
        Func<IReadOnlyList<WebhookConfig>> webhooks,
        WebhookSender sender,
        WebhookDeliveryOptions options,
        TimeProvider timeProvider,
        ILogger<WebhookEventDispatcher> logger)
    {
        _webhooks = webhooks;
        _sender = sender;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _channel = Channel.CreateBounded<WebhookEvent>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            dropped => _logger.LogWarning("Webhook queue is full; dropped a {Event} event.", dropped.Name));
    }

    public void Enqueue(WebhookEvent webhookEvent)
    {
        try
        {
            // Skip the queue entirely when nobody listens.
            if (_webhooks().Any(w => WebhookEvents.Subscribes(w, webhookEvent.Name)))
            {
                _channel.Writer.TryWrite(webhookEvent);
            }
        }
        catch (Exception ex)
        {
            // Producers have already done their work (e.g. saved a job); never fail them.
            _logger.LogError(ex, "Could not queue a {Event} webhook event.", webhookEvent.Name);
        }
    }

    /// <summary>Delivers queued events until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var webhookEvent in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await DeliverAsync(webhookEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad event must not stop the dispatcher (and with it the host).
                    _logger.LogError(ex, "Delivering a {Event} webhook event failed.", webhookEvent.Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_channel.Reader.Count > 0)
            {
                _logger.LogWarning("Stopping with {Count} undelivered webhook event(s).", _channel.Reader.Count);
            }
        }
    }

    /// <summary>Sends one event to every subscribed webhook; webhooks are served in parallel.</summary>
    public Task DeliverAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        var targets = _webhooks()
            .Where(w => WebhookEvents.Subscribes(w, webhookEvent.Name) && MatchesNames(w, webhookEvent))
            .Select(w => SendWithRetriesAsync(w, webhookEvent, cancellationToken));

        return Task.WhenAll(targets);
    }

    private async Task SendWithRetriesAsync(WebhookConfig webhook, WebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        var (body, query) = WebhookSender.Project(webhook, webhookEvent.Payload);
        var delay = _options.FirstRetryDelay;

        for (var attempt = 0; ; attempt++)
        {
            if (await _sender.SendAsync(webhook, body, query, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (attempt >= _options.MaxRetries)
            {
                _logger.LogError(
                    "Webhook '{Name}' did not accept a {Event} event after {Attempts} attempt(s); giving up.",
                    webhook.Name ?? webhook.Url, webhookEvent.Name, attempt + 1);
                return;
            }

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            delay *= 2;
        }
    }

    /// <summary>The <c>names</c> filter applies to events tied to a rule (job status).</summary>
    private static bool MatchesNames(WebhookConfig webhook, WebhookEvent webhookEvent) =>
        webhookEvent.Name != WebhookEvents.JobStatus
        || webhook.Names is not { Count: > 0 } names
        || (webhookEvent.RuleName is not null && names.Contains(webhookEvent.RuleName));
}
