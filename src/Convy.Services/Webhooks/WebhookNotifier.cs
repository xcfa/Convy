using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Webhooks;

/// <summary>
/// Sends a single POST per configured webhook at the end of a sync cycle.
/// The JSON body contains <c>linked</c> (array of torrent property objects)
/// and <c>errors</c> (array of <c>{hash, error}</c> objects).
/// When explicit <see cref="WebhookConfig.Params"/> are set, only the
/// configured body-place params appear in each linked item; query-place
/// params are appended to the URL.
/// When no params are configured, every torrent property is included.
/// </summary>
public sealed class WebhookNotifier : IWebhookNotifier
{
    private readonly IReadOnlyList<WebhookConfig> _webhooks;
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookNotifier> _logger;

    public WebhookNotifier(
        IReadOnlyList<WebhookConfig> webhooks,
        HttpClient httpClient,
        ILogger<WebhookNotifier> logger)
    {
        _webhooks = webhooks;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task NotifyAsync(WebhookBatch batch, CancellationToken cancellationToken)
    {
        if (_webhooks.Count == 0 || !batch.HasEntries)
        {
            return;
        }

        foreach (var webhook in _webhooks)
        {
            try
            {
                await SendAsync(webhook, batch, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Webhook '{Name}' sent successfully.", webhook.Name ?? webhook.Url);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook '{Name}' ({Url}) failed.", webhook.Name ?? "(unnamed)", webhook.Url);
            }
        }
    }

    private async Task SendAsync(
        WebhookConfig webhook,
        WebhookBatch batch,
        CancellationToken cancellationToken)
    {
        var hasExplicitParams = webhook.Params is { Count: > 0 };
        var queryParams = new Dictionary<string, string>();

        var linkedItems = new List<Dictionary<string, string>>();
        foreach (var properties in batch.Linked)
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

        var errorItems = batch.Errors.Select(e => new Dictionary<string, string>
        {
            ["hash"] = e.Hash,
            ["error"] = e.Message,
        }).ToList();

        var url = webhook.Url;
        if (queryParams.Count > 0)
        {
            var query = string.Join("&", queryParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            url += (url.Contains('?') ? '&' : '?') + query;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = JsonContent.Create(new { linked = linkedItems, errors = errorItems });

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Webhook '{Name}' returned {StatusCode}: {Body}",
                webhook.Name ?? webhook.Url, (int)response.StatusCode, body);
        }
    }
}
