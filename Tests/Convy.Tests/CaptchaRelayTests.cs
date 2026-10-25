using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Convy.Captcha;
using Convy.Services.Captcha;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Convy.Tests;

/// <summary>
/// The relay of the service of checks: Convy's client (on a test server) talks to <c>/api/captcha</c>, the relay talks to
/// a fake captcha-solver on a real loopback port with its own HTTP client and WebSocket, as in production.
/// </summary>
public sealed class CaptchaRelayTests : IAsyncLifetime
{
    private WebApplication _service = null!;
    private WebApplication _convy = null!;
    private Uri _serviceUrl = null!;
    private readonly List<(string Path, string? Authorization, string? Body)> _calls = [];
    private readonly CaptchaOptions _options = new() { Key = "convy-key" };

    public async Task InitializeAsync()
    {
        // The service: echoes the calls, a solver socket that greets and echoes, a screen it does not know.
        var service = WebApplication.CreateBuilder();
        service.WebHost.UseUrls("http://127.0.0.1:0");
        _service = service.Build();
        _service.UseWebSockets();
        _service.MapPost("/solve", async (HttpContext http) =>
        {
            Record(http, await new StreamReader(http.Request.Body).ReadToEndAsync());
            return Results.Json(new { id = Guid.Empty, state = "pending" }, statusCode: 200);
        });
        _service.MapGet("/challenges", (HttpContext http) =>
        {
            Record(http, null);
            return Results.Json(Array.Empty<object>());
        });
        _service.MapGet("/challenges/{id:guid}", (HttpContext http) =>
        {
            Record(http, null);
            return Results.Json(new { error = "no such challenge" }, statusCode: 404);
        });
        _service.MapGet("/solvers/connect", async (HttpContext http) =>
        {
            if (http.Request.Headers.Authorization != "Bearer convy-key")
            {
                http.Response.StatusCode = 403;
                return;
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync("""{"type":"ready"}"""u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[1024];
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync((WebSocketCloseStatus)4000, "bye", CancellationToken.None);
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, CancellationToken.None);
            }
        });
        _service.MapGet("/challenges/{id:guid}/screen", () => Results.Json(new { error = "no such challenge" }, statusCode: 404));
        await _service.StartAsync();
        _serviceUrl = new Uri(_service.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");

        var convy = WebApplication.CreateBuilder();
        convy.WebHost.UseTestServer();
        convy.Services.AddSingleton<IOptionsMonitor<CaptchaOptions>>(new Options(_options));
        convy.Services.AddSingleton<CaptchaRelay>();
        _convy = convy.Build();
        _convy.UseWebSockets();
        CaptchaRelay.Map(_convy);
        await _convy.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _convy.DisposeAsync();
        await _service.DisposeAsync();
    }

    private void Record(HttpContext http, string? body)
    {
        lock (_calls) _calls.Add((http.Request.Path + http.Request.QueryString, http.Request.Headers.Authorization, body));
    }

    private TestServer Convy => _convy.GetTestServer();

    [Fact]
    public async Task CallsGoToTheServiceWithConvysKeyAndComeBackAsTheyAre()
    {
        _options.Url = _serviceUrl.AbsoluteUri;
        using var client = Convy.CreateClient();

        var solved = await client.PostAsJsonAsync("/api/captcha/solve", new { url = "https://gallery.test/", wait = 0 });
        var listed = await client.GetAsync("/api/captcha/challenges?state=pending&domain=gallery.test");
        var unknown = await client.GetAsync($"/api/captcha/challenges/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, solved.StatusCode);
        Assert.Equal("pending", (string?)JsonNode.Parse(await solved.Content.ReadAsStringAsync())!["state"]);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/json; charset=utf-8", unknown.Content.Headers.ContentType?.ToString());
        Assert.Equal(("/solve", "Bearer convy-key"), (_calls[0].Path, _calls[0].Authorization));
        Assert.Contains("\"url\":\"https://gallery.test/\"", _calls[0].Body);
        Assert.Equal("/challenges?state=pending&domain=gallery.test", _calls[1].Path);
    }

    [Fact]
    public async Task WithoutAServiceTheRelayAnswers503()
    {
        _options.Url = null;
        using var client = Convy.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/captcha/solve", new { url = "https://gallery.test/" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
        Assert.Contains("captcha.url", await answer.Content.ReadAsStringAsync());
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task SocketsArePipedBothWaysAndARefusedOneIsRefusedWithTheSameStatus()
    {
        _options.Url = _serviceUrl.AbsoluteUri;

        using var solver = await Convy.CreateWebSocketClient().ConnectAsync(new Uri(Convy.BaseAddress, "/api/captcha/solvers/connect"), CancellationToken.None);
        Assert.Equal("""{"type":"ready"}""", await ReceiveTextAsync(solver));
        await solver.SendAsync("""{"type":"accept"}"""u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal("""{"type":"accept"}""", await ReceiveTextAsync(solver));
        // The client closes: the service hears it and closes too, with its own code.
        await solver.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        var closed = await solver.ReceiveAsync(new byte[16], CancellationToken.None);
        Assert.Equal((WebSocketCloseStatus)4000, closed.CloseStatus);

        // A check the service does not know: the client's handshake is a 404 too.
        var refused = await Assert.ThrowsAnyAsync<Exception>(() =>
            Convy.CreateWebSocketClient().ConnectAsync(new Uri(Convy.BaseAddress, $"/api/captcha/challenges/{Guid.NewGuid()}/screen"), CancellationToken.None));
        Assert.Contains("404", refused.Message);

        // A wrong key: the service's 403.
        _options.Key = "wrong";
        refused = await Assert.ThrowsAnyAsync<Exception>(() =>
            Convy.CreateWebSocketClient().ConnectAsync(new Uri(Convy.BaseAddress, "/api/captcha/solvers/connect"), CancellationToken.None));
        Assert.Contains("403", refused.Message);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[1024];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    private sealed class Options(CaptchaOptions value) : IOptionsMonitor<CaptchaOptions>
    {
        public CaptchaOptions CurrentValue => value;

        public CaptchaOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<CaptchaOptions, string?> listener) => null;
    }
}
