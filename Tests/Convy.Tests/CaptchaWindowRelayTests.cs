using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Convy.Captcha;
using Convy.Services.Captcha;
using Convy.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Convy.Tests;

/// <summary>
/// The sockets of the UI's checks: the browser (on a test server) talks to <c>/api/ui/captcha</c>, the relay to a fake
/// captcha-solver on a loopback port. The service echoes what reaches it, so the test sees what went up.
/// </summary>
public sealed class CaptchaWindowRelayTests : IAsyncLifetime
{
    private WebApplication _service = null!;
    private WebApplication _convy = null!;
    private Uri _serviceUrl = null!;
    private readonly CaptchaOptions _options = new() { Key = "convy-key" };
    private readonly Guid _known = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var service = WebApplication.CreateBuilder();
        service.WebHost.UseUrls("http://127.0.0.1:0");
        _service = service.Build();
        _service.UseWebSockets();
        _service.MapGet("/solvers/connect", (HttpContext http) => EchoAsync(http, """{"type":"ready"}"""));
        _service.MapGet("/challenges/{id:guid}/screen", (HttpContext http, Guid id) => id == _known
            ? EchoAsync(http, """{"type":"hello","width":2,"height":1}""")
            : Results.Json(new { error = "no such challenge" }, statusCode: 404).ExecuteAsync(http));
        await _service.StartAsync();
        _serviceUrl = new Uri(_service.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");

        var convy = WebApplication.CreateBuilder();
        convy.WebHost.UseTestServer();
        convy.Services.AddAuthorization(authorization => authorization.AddPolicy(UiSetup.Policy, policy => policy.RequireAssertion(_ => true)));
        convy.Services.AddSingleton<IOptionsMonitor<CaptchaOptions>>(new Options(_options));
        convy.Services.AddSingleton<CaptchaRelay>();
        convy.Services.AddSingleton(sp => new CaptchaWindowRelay(
            sp.GetRequiredService<CaptchaRelay>(),
            sp.GetRequiredService<IOptionsMonitor<CaptchaOptions>>(),
            () => ["Gallery.test", ".e-hentai.org", " "],
            NullLogger<CaptchaWindowRelay>.Instance));
        _convy = convy.Build();
        _convy.UseWebSockets();
        CaptchaWindowRelay.Map(_convy);
        await _convy.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _convy.DisposeAsync();
        await _service.DisposeAsync();
    }

    /// <summary>A socket that greets and then sends back every text message it gets, until the other side closes.</summary>
    private static async Task EchoAsync(HttpContext http, string greeting)
    {
        if (http.Request.Headers.Authorization != "Bearer convy-key")
        {
            http.Response.StatusCode = 403;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        await socket.SendAsync(Encoding.UTF8.GetBytes(greeting), WebSocketMessageType.Text, true, CancellationToken.None);
        await socket.SendAsync(new byte[] { 0xFF, 0xD8 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                return;
            }

            await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, CancellationToken.None);
        }
    }

    private TestServer Convy => _convy.GetTestServer();

    private Task<WebSocket> ConnectAsync(string path, string? origin = "http://localhost") =>
        ConnectAsync(path, origin, Convy);

    private static Task<WebSocket> ConnectAsync(string path, string? origin, TestServer server)
    {
        var client = server.CreateWebSocketClient();
        if (origin is not null)
        {
            client.ConfigureRequest = request => request.Headers.Origin = origin;
        }

        return client.ConnectAsync(new Uri(server.BaseAddress, path), CancellationToken.None);
    }

    [Fact]
    public async Task TheTabIsAnInteractiveSolverOfThePluginsDomainsAndOnlyAcceptOrDeclineGoUp()
    {
        _options.Url = _serviceUrl.AbsoluteUri;

        using var tab = await ConnectAsync("/api/ui/captcha/solvers/connect");

        Assert.Equal("""{"type":"ready"}""", await ReceiveTextAsync(tab));
        Assert.Equal(WebSocketMessageType.Binary, (await ReceiveAsync(tab)).Type);
        var hello = JsonNode.Parse(await ReceiveTextAsync(tab))!;
        Assert.Equal("hello", (string?)hello["type"]);
        Assert.Equal("convy", (string?)hello["name"]);
        Assert.Equal("interactive", (string?)hello["kind"]);
        Assert.Equal(["e-hentai.org", "gallery.test"], hello["domains"]!.AsArray().Select(d => (string?)d));

        // A tab cannot name itself again or register as an automatic solver; a repeated key or a non-object is dropped.
        await SendTextAsync(tab, """{"type":"hello","name":"evil","kind":"automatic","domains":["*"]}""");
        await SendTextAsync(tab, """{"type":"accept","type":"take","challengeId":"1"}""");
        await SendTextAsync(tab, """["accept"]""");
        await SendTextAsync(tab, """{"type":"take"}""");
        await SendTextAsync(tab, """{ "type": "decline", "challengeId": "2" }""");

        Assert.Equal("""{"type":"decline","challengeId":"2"}""", await ReceiveTextAsync(tab));

        await tab.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, (await ReceiveAsync(tab)).Type);
    }

    [Fact]
    public async Task TheWindowGetsTheFramesAndSendsOnlyItsInput()
    {
        _options.Url = _serviceUrl.AbsoluteUri;

        using var window = await ConnectAsync($"/api/ui/captcha/challenges/{_known}/screen");

        Assert.Contains("hello", await ReceiveTextAsync(window));
        Assert.Equal(WebSocketMessageType.Binary, (await ReceiveAsync(window)).Type);
        await SendTextAsync(window, """{"type":"accept","challengeId":"1"}""");
        // Parses, but cannot be written again: dropped, and the window goes on.
        await SendTextAsync(window, """{"type":"take","x":"\ud800"}""");
        await SendTextAsync(window, """{"type":"input","event":{"type":"move","x":1,"y":2}}""");
        Assert.Equal("""{"type":"input","event":{"type":"move","x":1,"y":2}}""", await ReceiveTextAsync(window));

        // A message too large for a window closes it.
        await window.SendAsync(new byte[20 * 1024], WebSocketMessageType.Text, true, CancellationToken.None);
        var closed = await ReceiveAsync(window);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, closed.Status);
    }

    [Fact]
    public async Task TheReasonsOfAClosedSocketHaveTheirCodes()
    {
        _options.Url = _serviceUrl.AbsoluteUri;
        using var unknown = await ConnectAsync($"/api/ui/captcha/challenges/{Guid.NewGuid()}/screen");
        Assert.Equal((WebSocketCloseStatus)CaptchaWindowRelay.CloseNotFound, (await ReceiveAsync(unknown)).Status);

        _options.Key = "wrong";
        using var refused = await ConnectAsync("/api/ui/captcha/solvers/connect");
        Assert.Equal((WebSocketCloseStatus)CaptchaWindowRelay.CloseUnavailable, (await ReceiveAsync(refused)).Status);

        _options.Url = null;
        using var notSetUp = await ConnectAsync("/api/ui/captcha/solvers/connect");
        Assert.Equal((WebSocketCloseStatus)CaptchaWindowRelay.CloseNotSetUp, (await ReceiveAsync(notSetUp)).Status);
    }

    [Fact]
    public async Task OnlyPagesOfConvysOwnHostOpenTheSockets()
    {
        _options.Url = _serviceUrl.AbsoluteUri;

        var foreign = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("/api/ui/captcha/solvers/connect", "http://localhost:8080"));
        Assert.Contains("403", foreign.Message);
        using var noOrigin = await ConnectAsync("/api/ui/captcha/solvers/connect", null);
        Assert.Equal("""{"type":"ready"}""", await ReceiveTextAsync(noOrigin));

        using var client = Convy.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("/api/ui/captcha/solvers/connect")).StatusCode);
    }

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var (type, text, _) = await ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Text, type);
        return text;
    }

    private static async Task<(WebSocketMessageType Type, string Text, WebSocketCloseStatus? Status)> ReceiveAsync(WebSocket socket)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        var count = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), limit.Token);
            count += result.Count;
        }
        while (!result.EndOfMessage);

        return (result.MessageType, Encoding.UTF8.GetString(buffer, 0, count), result.CloseStatus);
    }

    private sealed class Options(CaptchaOptions value) : IOptionsMonitor<CaptchaOptions>
    {
        public CaptchaOptions CurrentValue => value;

        public CaptchaOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<CaptchaOptions, string?> listener) => null;
    }
}
