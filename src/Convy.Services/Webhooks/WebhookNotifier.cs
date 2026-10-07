namespace Convy.Services.Webhooks;

/// <summary>
/// Sends a single POST per configured webhook at the end of a sync cycle (the
/// <c>linked</c> event). The JSON body contains <c>linked</c> (array of item property
/// objects) and <c>errors</c> (array of <c>{hash, error}</c> objects).
/// When explicit <see cref="WebhookConfig.Params"/> are set, only the
/// configured body-place params appear in each linked item; query-place
/// params are appended to the URL.
/// When no params are configured, every item property is included.
/// Only webhooks subscribed to <c>linked</c> (the default) are called.
/// </summary>
public sealed class WebhookNotifier : IWebhookNotifier
{
    private readonly Func<IReadOnlyList<WebhookConfig>> _webhooks;
    private readonly WebhookSender _sender;

    /// <param name="webhooks">Returns the current webhook configuration (it may be reloaded).</param>
    /// <param name="sender">Sends the requests.</param>
    public WebhookNotifier(Func<IReadOnlyList<WebhookConfig>> webhooks, WebhookSender sender)
    {
        _webhooks = webhooks;
        _sender = sender;
    }

    public async Task NotifyAsync(WebhookBatch batch, CancellationToken cancellationToken)
    {
        if (!batch.HasEntries)
        {
            return;
        }

        foreach (var webhook in _webhooks().Where(w => WebhookEvents.Subscribes(w, WebhookEvents.Linked)))
        {
            var linked = FilterLinked(webhook, batch.Linked);

            // Skip webhooks whose rule filter matched nothing this cycle and
            // that have no errors to report.
            if (linked.Count == 0 && batch.Errors.Count == 0)
            {
                continue;
            }

            var (body, query) = Build(webhook, linked, batch.Errors);
            await _sender.SendAsync(webhook, body, query, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Selects the linked items a webhook should receive. A webhook with an
    /// empty <see cref="WebhookConfig.Names"/> list receives every linked item;
    /// otherwise only items routed by a rule whose name is in the list.
    /// </summary>
    private static List<WebhookLinkedItem> FilterLinked(
        WebhookConfig webhook, IReadOnlyList<WebhookLinkedItem> linked)
    {
        if (webhook.Names is not { Count: > 0 } names)
        {
            return [.. linked];
        }

        return linked
            .Where(l => l.RuleName is not null && names.Contains(l.RuleName))
            .ToList();
    }

    private static (object Body, IReadOnlyDictionary<string, string> Query) Build(
        WebhookConfig webhook,
        IReadOnlyList<WebhookLinkedItem> linked,
        IReadOnlyList<WebhookError> errors)
    {
        var hasExplicitParams = webhook.Params is { Count: > 0 };
        var queryParams = new Dictionary<string, string>();

        var linkedItems = new List<Dictionary<string, string>>();
        foreach (var (_, properties) in linked)
        {
            var item = new Dictionary<string, string>();

            if (!hasExplicitParams)
            {
                foreach (var (key, value) in properties)
                {
                    item[key] = value;
                }
            }
            else
            {
                foreach (var param in webhook.Params!)
                {
                    var resolved = properties.GetValueOrDefault(param.Value, "");
                    if (param.Place == WebhookParamPlace.Body)
                    {
                        item[param.Name] = resolved;
                    }
                    else
                    {
                        queryParams[param.Name] = resolved;
                    }
                }
            }

            linkedItems.Add(item);
        }

        var errorItems = errors.Select(e => new Dictionary<string, string>
        {
            ["hash"] = e.Hash,
            ["error"] = e.Message,
        }).ToList();

        return (new { linked = linkedItems, errors = errorItems }, queryParams);
    }
}
