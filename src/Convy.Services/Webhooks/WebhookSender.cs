using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Convy.Services.Webhooks;

/// <summary>Sends one webhook request; shared by the batch notifier and the event dispatcher.</summary>
public sealed class WebhookSender
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookSender> _logger;

    public WebhookSender(HttpClient httpClient, ILogger<WebhookSender> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// POSTs <paramref name="body"/> as JSON to the webhook's URL plus <paramref name="query"/>.
    /// Returns <c>true</c> on a success status; logs and returns <c>false</c> otherwise.
    /// Only cancellation is thrown.
    /// </summary>
    public async Task<bool> SendAsync(
        WebhookConfig webhook, object body, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        var url = webhook.Url;
        if (query.Count > 0)
        {
            var queryString = string.Join("&", query.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            url += (url.Contains('?') ? '&' : '?') + queryString;
        }

        try
        {
            // A buffered body with Content-Length: some receivers and proxies reject chunked requests.
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Webhook '{Name}' sent successfully.", webhook.Name ?? webhook.Url);
                return true;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Webhook '{Name}' returned {StatusCode}: {Body}",
                webhook.Name ?? webhook.Url, (int)response.StatusCode, responseBody);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook '{Name}' ({Url}) failed.", webhook.Name ?? "(unnamed)", webhook.Url);
            return false;
        }
    }

    /// <summary>
    /// The body and query of an event for a webhook: the whole payload, or only the fields
    /// selected by its <see cref="WebhookConfig.Params"/>.
    /// </summary>
    public static (object Body, IReadOnlyDictionary<string, string> Query) Project(
        WebhookConfig webhook, IReadOnlyDictionary<string, object?> payload)
    {
        if (webhook.Params is not { Count: > 0 } parameters)
        {
            return (payload, new Dictionary<string, string>());
        }

        var lookup = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);
        var body = new Dictionary<string, object?>();
        var query = new Dictionary<string, string>();

        foreach (var param in parameters)
        {
            var value = lookup.GetValueOrDefault(param.Value);
            if (param.Place == WebhookParamPlace.Body)
            {
                body[param.Name] = value;
            }
            else
            {
                query[param.Name] = Stringify(value);
            }
        }

        return (body, query);
    }

    private static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable items => string.Join(",", items.Cast<object?>().Select(Stringify)),
        _ => value.ToString() ?? string.Empty,
    };
}
