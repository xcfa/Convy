using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Captcha;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Convy.Captcha;

/// <summary>
/// <c>/api/captcha</c>: the API of the service of checks (captcha-solver) for Convy's clients, forwarded with Convy's key —
/// calls (<c>/solve</c>, <c>/challenges</c>, <c>/fetch</c>) and WebSockets (<c>/solvers/connect</c>,
/// <c>/challenges/{id}/screen</c>). A client cannot tell it from the service: Shizuka set up with Convy's address
/// registers its tab as a solver and shows the page of a check through it, while the cookies stay with Convy, which goes
/// to the sites. Behind the IP allow-list and the API key, like the rest of the external API. Convy's own profiles at
/// the service (its extensions' sign-ins) stay its own: a client cannot name them, and their cookies are left out of the
/// answers about Convy's checks.
/// </summary>
public sealed class CaptchaRelay : IDisposable
{
    /// <summary>Where the relay is mapped.</summary>
    public const string Prefix = "/api/captcha";

    private static readonly string[] Forwarded = ["content-type", "retry-after"];

    /// <summary>The largest body of a call or an answer the relay reads to keep Convy's profiles to itself.</summary>
    private const int MaxJsonBytes = 4 * 1024 * 1024;

    private readonly IOptionsMonitor<CaptchaOptions> _options;
    private readonly HttpClient _http;

    public CaptchaRelay(IOptionsMonitor<CaptchaOptions> options)
        : this(options, new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <param name="handler">The transport of the calls; tests pass a fake service.</param>
    public CaptchaRelay(IOptionsMonitor<CaptchaOptions> options, HttpMessageHandler handler)
    {
        _options = options;
        // /solve waits up to a minute, /fetch up to its own timeout: the caller's own limits end a call sooner.
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(6) };
    }

    /// <summary>
    /// Opens the service's WebSocket with the key; a handshake the service answered with a status is a
    /// <see cref="RefusedException"/>. Tests swap the whole call.
    /// </summary>
    public Func<Uri, string, CancellationToken, Task<WebSocket>> ConnectWebSocket { get; set; } = ConnectClientAsync;

    /// <summary>Maps the relay's routes at <see cref="Prefix"/>; the app needs <c>UseWebSockets</c>.</summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix);
        group.MapPost("/solve", (HttpContext http, CaptchaRelay relay) => relay.ForwardAsync(http, "solve"));
        group.MapGet("/challenges", (HttpContext http, CaptchaRelay relay) => relay.ForwardAsync(http, "challenges"));
        group.MapGet("/challenges/{id:guid}", (HttpContext http, Guid id, CaptchaRelay relay) => relay.ForwardAsync(http, $"challenges/{id}"));
        group.MapPost("/challenges/{id:guid}/cancel", (HttpContext http, Guid id, CaptchaRelay relay) => relay.ForwardAsync(http, $"challenges/{id}/cancel"));
        group.MapPost("/fetch", (HttpContext http, CaptchaRelay relay) => relay.ForwardAsync(http, "fetch"));
        group.MapGet("/solvers/connect", (HttpContext http, CaptchaRelay relay) => relay.PipeAsync(http, "solvers/connect"));
        group.MapGet("/challenges/{id:guid}/screen", (HttpContext http, Guid id, CaptchaRelay relay) => relay.PipeAsync(http, $"challenges/{id}/screen"));
    }

    private async Task ForwardAsync(HttpContext http, string path)
    {
        if (_options.CurrentValue.Upstream is not { } upstream)
        {
            await ProblemAsync(http, StatusCodes.Status503ServiceUnavailable, "No service of checks is set up (captcha.url, captcha.key).").ConfigureAwait(false);
            return;
        }

        var (service, key) = upstream;

        var options = _options.CurrentValue;
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), new Uri(service, path + http.Request.QueryString));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (path is "solve" or "fetch")
        {
            // What goes up is the parsed call, so the service reads the same profile as this check did.
            var call = await ReadJsonAsync(http.Request.Body, http.RequestAborted).ConfigureAwait(false);
            if (call is not JsonObject body)
            {
                await ProblemAsync(http, StatusCodes.Status400BadRequest, "The body must be a JSON object.").ConfigureAwait(false);
                return;
            }

            if (options.IsOwn(Text(body["profile"])))
            {
                await ProblemAsync(http, StatusCodes.Status403Forbidden, "Convy's own profiles at the service are not for its clients.").ConfigureAwait(false);
                return;
            }

            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        }
        else if (HttpMethods.IsPost(http.Request.Method))
        {
            request.Content = new StreamContent(http.Request.Body);
            if (http.Request.ContentType is { } type)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            }
        }

        HttpResponseMessage answer;
        try
        {
            answer = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            await ProblemAsync(http, StatusCodes.Status502BadGateway, "The service of checks cannot be reached: " + ex.Message).ConfigureAwait(false);
            return;
        }
        catch (TaskCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            await ProblemAsync(http, StatusCodes.Status504GatewayTimeout, "The service of checks did not answer in time.").ConfigureAwait(false);
            return;
        }

        using (answer)
        {
            http.Response.StatusCode = (int)answer.StatusCode;
            foreach (var name in Forwarded)
            {
                if (answer.Content.Headers.TryGetValues(name, out var values) || answer.Headers.TryGetValues(name, out values))
                {
                    http.Response.Headers[name] = values.ToArray();
                }
            }

            if (path is "fetch" || !answer.IsSuccessStatusCode)
            {
                await answer.Content.CopyToAsync(http.Response.Body, http.RequestAborted).ConfigureAwait(false);
                return;
            }

            // An answer about checks: those of Convy's own profiles go without their cookies.
            var about = await ReadJsonAsync(await answer.Content.ReadAsStreamAsync(http.RequestAborted).ConfigureAwait(false), http.RequestAborted)
                .ConfigureAwait(false);
            foreach (var check in about switch { JsonArray list => list.OfType<JsonObject>(), JsonObject one => [one], _ => [] })
            {
                if (options.IsOwn(Text(check["profile"])))
                {
                    check.Remove("cookies");
                }
            }

            http.Response.Headers.ContentLength = null;
            await http.Response.WriteAsync(about?.ToJsonString() ?? "null", http.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>A JSON body of at most <see cref="MaxJsonBytes"/>; null — not JSON, too large, or with a key twice.</summary>
    private static async Task<JsonNode?> ReadJsonAsync(System.IO.Stream body, CancellationToken ct)
    {
        using var buffer = new System.IO.MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxJsonBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            var node = JsonNode.Parse(buffer.ToArray());
            // A repeated key would let two parsers read two profiles: building the object's properties refuses it.
            if (node is JsonObject call)
            {
                _ = call.Count;
            }

            return node;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private async Task PipeAsync(HttpContext http, string path)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "A WebSocket request is expected.").ConfigureAwait(false);
            return;
        }

        if (_options.CurrentValue.Upstream is not { } upstream)
        {
            await ProblemAsync(http, StatusCodes.Status503ServiceUnavailable, "No service of checks is set up (captcha.url, captcha.key).").ConfigureAwait(false);
            return;
        }

        var (service, key) = upstream;

        var address = new UriBuilder(new Uri(service, path)) { Scheme = service.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        WebSocket outer;
        try
        {
            outer = await ConnectWebSocket(address, key, http.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RefusedException or WebSocketException or HttpRequestException or InvalidOperationException)
        {
            // Refused by the service (an unknown check, a key without the solver scope): the client's handshake is
            // refused with the same status, as if it had asked the service itself.
            var status = ex is RefusedException { Status: var refused } && (int)refused is >= 400 and < 600 ? (int)refused : StatusCodes.Status502BadGateway;
            await ProblemAsync(http, status, "The service of checks refused the socket: " + ex.Message).ConfigureAwait(false);
            return;
        }

        using (outer)
        using (var inner = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false))
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            var down = CopyAsync(outer, inner, stop.Token);
            var up = CopyAsync(inner, outer, stop.Token);
            await Task.WhenAny(down, up).ConfigureAwait(false);
            // One side closed and its close went on: the other's answer to it ends the second copy. A cancelled receive
            // would abort the socket instead of closing it.
            await Task.WhenAny(Task.WhenAll(down, up), Task.Delay(TimeSpan.FromSeconds(5), http.RequestAborted)).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Quietly(down), Quietly(up)).ConfigureAwait(false);
            await CloseAsync(inner, down.IsFaulted ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
            await CloseAsync(outer, WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
        }
    }

    /// <summary>Messages from <paramref name="from"/> to <paramref name="to"/> as they are, until either side closes.</summary>
    private static async Task CopyAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await from.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // The other side may have closed first: the close still answers it, with this side's code.
                if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await to.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription, ct).ConfigureAwait(false);
                }

                return;
            }

            await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct).ConfigureAwait(false);
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseAsync(status, "", limit.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }

    private static async Task<WebSocket> ConnectClientAsync(Uri url, string key, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await socket.ConnectAsync(url, limit.Token).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException ex) when (socket.HttpStatusCode != 0)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            throw new RefusedException(status, ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new WebSocketException("The service of checks did not open the socket in 15 s.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Task ProblemAsync(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { error = message });
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The service answered the handshake with <paramref name="status"/> instead of opening the socket.</summary>
    public sealed class RefusedException(HttpStatusCode status, Exception? inner = null)
        : Exception($"The service of checks answered {(int)status}.", inner)
    {
        public HttpStatusCode Status { get; } = status;
    }
}
