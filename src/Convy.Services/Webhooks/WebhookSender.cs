using System.Collections;
using System.Diagnostics;
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
        var delivery = await DeliverAsync(webhook, body, query, test: false, cancellationToken).ConfigureAwait(false);
        var name = webhook.Name ?? webhook.Url;

        if (delivery.Success)
        {
            _logger.LogInformation("Webhook '{Name}' sent successfully.", name);
        }
        else if (delivery.Exception is not null)
        {
            _logger.LogError(delivery.Exception, "Webhook '{Name}' ({Url}) failed.", webhook.Name ?? "(unnamed)", webhook.Url);
        }
        else
        {
            _logger.LogWarning("Webhook '{Name}' returned {StatusCode}: {Body}", name, delivery.StatusCode, delivery.ResponseBody);
        }

        return delivery.Success;
    }

    /// <summary>
    /// Sends one request and reports what happened: status, response body (cut to
    /// <see cref="MaxResponseLength"/> characters), time taken, or the error. With
    /// <paramref name="test"/> the request carries an <c>X-Convy-Test: true</c> header.
    /// Only cancellation is thrown.
    /// </summary>
    public async Task<WebhookDelivery> DeliverAsync(
        WebhookConfig webhook, object body, IReadOnlyDictionary<string, string> query, bool test, CancellationToken cancellationToken)
    {
        var url = webhook.Url;
        if (query.Count > 0)
        {
            var queryString = string.Join("&", query.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            url += (url.Contains('?') ? '&' : '?') + queryString;
        }

        var json = JsonSerializer.Serialize(body);
        var started = Stopwatch.GetTimestamp();

        try
        {
            // A buffered body with Content-Length: some receivers and proxies reject chunked requests.
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (test)
            {
                request.Headers.Add("X-Convy-Test", "true");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new WebhookDelivery(
                url, json, (int)response.StatusCode, Cut(responseBody), null, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WebhookDelivery(url, json, null, null, ex, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>The longest response body kept in a <see cref="WebhookDelivery"/>.</summary>
    public const int MaxResponseLength = 4000;

    private static string Cut(string text) => text.Length > MaxResponseLength ? text[..MaxResponseLength] + "…" : text;

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

/// <summary>What happened to one webhook request.</summary>
/// <param name="Url">The URL called, query included.</param>
/// <param name="RequestBody">The JSON body sent.</param>
/// <param name="StatusCode">The response status, or <c>null</c> when no response arrived.</param>
/// <param name="ResponseBody">The response body, possibly cut.</param>
/// <param name="Exception">Why no response arrived (connection refused, timeout, …).</param>
/// <param name="Duration">Time until the response or the failure.</param>
public sealed record WebhookDelivery(
    string Url, string RequestBody, int? StatusCode, string? ResponseBody, Exception? Exception, TimeSpan Duration)
{
    public bool Success => StatusCode is >= 200 and < 300;
}
