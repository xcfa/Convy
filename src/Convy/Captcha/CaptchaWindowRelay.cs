using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Captcha;
using Convy.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Captcha;

/// <summary>
/// <c>/api/ui/captcha</c>: the sockets of the web UI's checks. While the UI is open its tab is a solver at the service of
/// checks for the plugins' domains (an offer is a banner, «Pass» opens the window with the page of the check). Unlike
/// <see cref="CaptchaRelay"/> for API clients, this one is behind the UI's sign-in and lets only the few messages a
/// window needs go up: a tab cannot register as an automatic solver or name itself.
/// </summary>
public sealed class CaptchaWindowRelay
{
    /// <summary>Where the sockets are mapped.</summary>
    public const string Prefix = "/api/ui/captcha";

    /// <summary>The solver's name at the service (its settings there are by name: <c>Solvers:convy</c>).</summary>
    public const string SolverName = "convy";

    /// <summary>The check the screen socket asked for is unknown to the service (or over).</summary>
    public const int CloseNotFound = 4404;

    /// <summary>No service of checks is set up (<c>captcha.url</c>, <c>captcha.key</c>).</summary>
    public const int CloseNotSetUp = 4409;

    /// <summary>The service refused the socket, did not answer or broke it.</summary>
    public const int CloseUnavailable = 4502;

    /// <summary>A message from the browser larger than this closes its socket (the window's are a few hundred bytes).</summary>
    private const int MaxUpMessage = 16 * 1024;

    private static readonly HashSet<string> SolverUp = ["accept", "decline"];
    private static readonly HashSet<string> ScreenUp = ["take", "input", "done"];

    private readonly CaptchaRelay _relay;
    private readonly IOptionsMonitor<CaptchaOptions> _options;
    private readonly Func<IEnumerable<string>> _domains;
    private readonly ILogger<CaptchaWindowRelay> _logger;

    /// <param name="relay">Opens the service's sockets with Convy's key.</param>
    /// <param name="domains">The plugins' domains, the ones the tab passes checks for.</param>
    public CaptchaWindowRelay(
        CaptchaRelay relay, IOptionsMonitor<CaptchaOptions> options, Func<IEnumerable<string>> domains, ILogger<CaptchaWindowRelay> logger)
    {
        _relay = relay;
        _options = options;
        _domains = domains;
        _logger = logger;
    }

    /// <summary>Maps the sockets at <see cref="Prefix"/> behind the UI's policy; the app needs <c>UseWebSockets</c>.</summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix).RequireAuthorization(UiSetup.Policy).ExcludeFromDescription();
        group.MapGet("/solvers/connect", (HttpContext http, CaptchaWindowRelay relay) => relay.SolverAsync(http));
        group.MapGet("/challenges/{id:guid}/screen", (HttpContext http, Guid id, CaptchaWindowRelay relay) => relay.ScreenAsync(http, id));
    }

    private async Task SolverAsync(HttpContext http)
    {
        if (await AcceptAsync(http).ConfigureAwait(false) is not { } client)
        {
            return;
        }

        using (client)
        {
            await using var link = await OpenAsync(client, "solvers/connect", CloseUnavailable, http.RequestAborted).ConfigureAwait(false);
            if (link is null)
            {
                return;
            }

            try
            {
                await link.SendUpAsync(new JsonObject
                {
                    ["type"] = "hello",
                    ["name"] = SolverName,
                    ["kind"] = "interactive",
                    ["domains"] = new JsonArray(Domains().Select(d => (JsonNode?)d).ToArray()),
                }, http.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (WebSocketException)
            {
                // Closed at once (a service without solvers): the tab hears it as unavailable.
                await CloseAsync(client, CloseUnavailable, "the service of checks is unavailable").ConfigureAwait(false);
                return;
            }

            await link.RunAsync(SolverUp, http.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task ScreenAsync(HttpContext http, Guid id)
    {
        if (await AcceptAsync(http).ConfigureAwait(false) is not { } client)
        {
            return;
        }

        using (client)
        {
            await using var link = await OpenAsync(client, $"challenges/{id}/screen", CloseNotFound, http.RequestAborted).ConfigureAwait(false);
            if (link is null)
            {
                return;
            }

            await link.RunAsync(ScreenUp, http.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The browser's socket, from a page of this host and port (a missing <c>Origin</c> is not a browser's); otherwise
    /// 400 or 403. A WebSocket carries no <see cref="UiSetup.RequestHeader"/>, and another app on another port of the
    /// same host gets the session cookie too (cookies ignore ports).
    /// </summary>
    private static async Task<WebSocket?> AcceptAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "A WebSocket request is expected." }).ConfigureAwait(false);
            return null;
        }

        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0
            && !(Uri.TryCreate(origin, UriKind.Absolute, out var from)
                && string.Equals(from.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return null;
        }

        return await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
    }

    /// <summary>The plugins' domains, without a leading dot, in lower case.</summary>
    private SortedSet<string> Domains() =>
        new(_domains().Select(d => d.Trim().TrimStart('.').ToLowerInvariant()).Where(d => d.Length > 0), StringComparer.Ordinal);

    /// <summary>The service's socket at <paramref name="path"/>; null — the browser's is closed with the reason.</summary>
    private async Task<Link?> OpenAsync(WebSocket client, string path, int notFound, CancellationToken ct)
    {
        if (_options.CurrentValue.Upstream is not { } upstream)
        {
            await CloseAsync(client, CloseNotSetUp, "the service of checks is not set up").ConfigureAwait(false);
            return null;
        }

        var (service, key) = upstream;
        var url = new UriBuilder(new Uri(service, path)) { Scheme = service.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        try
        {
            return new Link(client, await _relay.ConnectWebSocket(url, key, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The tab left while the service's socket was opening.
        }
        catch (CaptchaRelay.RefusedException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            await CloseAsync(client, notFound, "the service of checks does not know it").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CaptchaRelay.RefusedException or WebSocketException or HttpRequestException or IOException
            or InvalidOperationException)
        {
            _logger.LogWarning("The service of checks refused or did not answer the socket {Path}: {Message}", path, ex.Message);
            await CloseAsync(client, CloseUnavailable, "the service of checks is unavailable").ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Closes and waits a little for the other side's answer (a peer that never answers is dropped).</summary>
    private static async Task CloseAsync(WebSocket socket, int code, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync((WebSocketCloseStatus)code, reason, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }

    /// <summary>The browser's socket and the service's, relayed until either side closes.</summary>
    private sealed class Link(WebSocket client, WebSocket upstream) : IAsyncDisposable
    {
        // The browser's messages and the hello both go up, the service's messages and the relay's closes both go
        // down: one send at a time on each socket.
        private readonly SemaphoreSlim _up = new(1, 1);
        private readonly SemaphoreSlim _down = new(1, 1);

        public Task SendUpAsync(JsonNode message, CancellationToken ct) =>
            SendUpAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), ct);

        private async Task SendUpAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            await _up.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await upstream.SendAsync(message, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _up.Release();
            }
        }

        public async Task RunAsync(IReadOnlySet<string> allowed, CancellationToken ct)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var down = DownAsync(stop.Token);
            var up = UpAsync(allowed, stop.Token);
            await Task.WhenAny(down, up).ConfigureAwait(false);
            // The service's socket broke (a restart, a reset): the browser hears it now, not as an abort later.
            if (down.IsFaulted)
            {
                await CloseDownAsync((WebSocketCloseStatus)CloseUnavailable, "the service of checks is unavailable").ConfigureAwait(false);
            }

            // The browser's socket broke (a tab gone without a close): the service hears it now too.
            if (up.IsFaulted)
            {
                await Quietly(CloseUpAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)).ConfigureAwait(false);
            }

            // One side closed and its close went on: the other's answer to it ends the second copy. A cancelled
            // receive would abort the socket instead of closing it.
            await Task.WhenAny(Task.WhenAll(down, up), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Quietly(down), Quietly(up)).ConfigureAwait(false);
            await CloseAsync(client, (int)WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);
            await CloseAsync(upstream, (int)WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);
        }

        /// <summary>The service's messages to the browser as they are (frames come in pieces), until either closes.</summary>
        private async Task DownAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var result = await upstream.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseDownAsync(upstream.CloseStatus ?? WebSocketCloseStatus.NormalClosure, upstream.CloseStatusDescription).ConfigureAwait(false);
                    return;
                }

                await _down.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await client.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct).ConfigureAwait(false);
                }
                finally
                {
                    _down.Release();
                }
            }
        }

        /// <summary>The browser's messages to the service: whole JSON objects whose <c>type</c> is allowed, the rest dropped.</summary>
        private async Task UpAsync(IReadOnlySet<string> allowed, CancellationToken ct)
        {
            var buffer = new byte[MaxUpMessage];
            while (true)
            {
                var count = 0;
                ValueWebSocketReceiveResult result;
                while (true)
                {
                    result = await client.ReceiveAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
                    count += result.Count;
                    if (result.EndOfMessage || result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (count == buffer.Length)
                    {
                        await CloseDownAsync(WebSocketCloseStatus.MessageTooBig, "message too big").ConfigureAwait(false);
                        await CloseUpAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
                        return;
                    }
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // The browser's own code and reason stay here: the service hears a plain close.
                    await CloseUpAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Text && Allowed(buffer.AsMemory(0, count), allowed) is { } message)
                {
                    await SendUpAsync(message, ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Closes the browser's output (once; a socket closed already is left as it is).</summary>
        private async Task CloseDownAsync(WebSocketCloseStatus status, string? description)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _down.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (client.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await client.CloseOutputAsync(status, description, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
            }
            finally
            {
                _down.Release();
            }
        }

        private async Task CloseUpAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            await _up.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (upstream.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await upstream.CloseOutputAsync(status, description, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _up.Release();
            }
        }

        /// <summary>
        /// The message as it goes up, written again from what was checked (the service reads JSON its own way, so the
        /// bytes it gets are the ones judged); null — not a JSON object with an allowed <c>type</c>, or a duplicate key.
        /// </summary>
        private static byte[]? Allowed(ReadOnlyMemory<byte> message, IReadOnlySet<string> allowed)
        {
            try
            {
                // Written inside the check: a lone surrogate parses but does not write, and is dropped like the rest.
                return JsonNode.Parse(message.Span) is JsonObject node
                    && node["type"] is JsonValue type && type.TryGetValue<string>(out var name) && allowed.Contains(name)
                    ? Encoding.UTF8.GetBytes(node.ToJsonString())
                    : null;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }

        private static async Task Quietly(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
            }
        }

        public ValueTask DisposeAsync()
        {
            upstream.Dispose();
            _up.Dispose();
            _down.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
