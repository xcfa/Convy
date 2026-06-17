using System.Net;
using System.Text.Json;
using Convy.Services.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class WebhookNotifierTests
{
    private static readonly IReadOnlyDictionary<string, string> Torrent1 =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hash"] = "abc123",
            ["name"] = "Test Torrent",
            ["category"] = "Movies",
            ["savePath"] = "/data/downloads",
            ["targetPath"] = "/data/media/movies",
            ["size"] = "1073741824",
            ["state"] = "StalledUpload",
            ["tags"] = "hd,action",
        };

    private static readonly IReadOnlyDictionary<string, string> Torrent2 =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hash"] = "def456",
            ["name"] = "Another Torrent",
            ["category"] = "Anime",
            ["savePath"] = "/data/downloads",
            ["targetPath"] = "/data/media/anime",
            ["size"] = "524288",
            ["state"] = "Uploading",
            ["tags"] = "sub",
        };

    private static WebhookNotifier Create(
        IReadOnlyList<WebhookConfig> configs,
        FakeHandler handler) =>
        new(configs, new HttpClient(handler), NullLogger<WebhookNotifier>.Instance);

    private static WebhookBatch BatchWith(
        IReadOnlyDictionary<string, string>[]? linked = null,
        WebhookError[]? errors = null)
    {
        var batch = new WebhookBatch();
        if (linked is not null)
            foreach (var item in linked) batch.Linked.Add(item);
        if (errors is not null)
            foreach (var err in errors) batch.Errors.Add(err);
        return batch;
    }

    [Fact]
    public async Task NoWebhooks_DoesNothing()
    {
        var handler = new FakeHandler();
        var notifier = Create([], handler);

        await notifier.NotifyAsync(BatchWith([Torrent1]), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EmptyBatch_DoesNothing()
    {
        var handler = new FakeHandler();
        var notifier = Create([new WebhookConfig { Url = "https://example.com/hook" }], handler);

        await notifier.NotifyAsync(new WebhookBatch(), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NoParams_SendsAllPropertiesInBody()
    {
        var handler = new FakeHandler();
        var notifier = Create([new WebhookConfig { Url = "https://example.com/hook" }], handler);

        await notifier.NotifyAsync(BatchWith([Torrent1, Torrent2]), CancellationToken.None);

        Assert.Single(handler.Requests);
        var (method, uri, body) = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("", uri.Query);

        var json = JsonSerializer.Deserialize<JsonElement>(body!);
        var linked = json.GetProperty("linked");
        Assert.Equal(2, linked.GetArrayLength());
        Assert.Equal("abc123", linked[0].GetProperty("hash").GetString());
        Assert.Equal("Movies", linked[0].GetProperty("category").GetString());
        Assert.Equal("def456", linked[1].GetProperty("hash").GetString());
        Assert.Equal("Anime", linked[1].GetProperty("category").GetString());

        var errors = json.GetProperty("errors");
        Assert.Equal(0, errors.GetArrayLength());
    }

    [Fact]
    public async Task ErrorsIncludedInBody()
    {
        var handler = new FakeHandler();
        var notifier = Create([new WebhookConfig { Url = "https://example.com/hook" }], handler);

        var batch = BatchWith(
            linked: [Torrent1],
            errors: [new WebhookError("bad789", "Link failed")]);

        await notifier.NotifyAsync(batch, CancellationToken.None);

        var json = JsonSerializer.Deserialize<JsonElement>(handler.Requests[0].Body!);
        var linked = json.GetProperty("linked");
        Assert.Equal(1, linked.GetArrayLength());

        var errors = json.GetProperty("errors");
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal("bad789", errors[0].GetProperty("hash").GetString());
        Assert.Equal("Link failed", errors[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task ErrorsOnly_StillSendsWebhook()
    {
        var handler = new FakeHandler();
        var notifier = Create([new WebhookConfig { Url = "https://example.com/hook" }], handler);

        var batch = BatchWith(errors: [new WebhookError("bad789", "Boom")]);

        await notifier.NotifyAsync(batch, CancellationToken.None);

        Assert.Single(handler.Requests);
        var json = JsonSerializer.Deserialize<JsonElement>(handler.Requests[0].Body!);
        Assert.Equal(0, json.GetProperty("linked").GetArrayLength());
        Assert.Equal(1, json.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task ExplicitBodyParams_OnlyIncludesConfigured()
    {
        var handler = new FakeHandler();
        var config = new WebhookConfig
        {
            Url = "https://example.com/hook",
            Params =
            [
                new WebhookParam { Place = WebhookParamPlace.Body, Name = "cat", Value = "category" },
                new WebhookParam { Place = WebhookParamPlace.Body, Name = "h", Value = "hash" },
            ]
        };

        var notifier = Create([config], handler);
        await notifier.NotifyAsync(BatchWith([Torrent1]), CancellationToken.None);

        var json = JsonSerializer.Deserialize<JsonElement>(handler.Requests[0].Body!);
        var item = json.GetProperty("linked")[0];
        Assert.Equal("Movies", item.GetProperty("cat").GetString());
        Assert.Equal("abc123", item.GetProperty("h").GetString());

        Assert.False(item.TryGetProperty("name", out _));
        Assert.False(item.TryGetProperty("savePath", out _));
    }

    [Fact]
    public async Task QueryParams_AppendedToUrl()
    {
        var handler = new FakeHandler();
        var config = new WebhookConfig
        {
            Url = "https://example.com/hook",
            Params =
            [
                new WebhookParam { Place = WebhookParamPlace.Query, Name = "cat", Value = "category" },
                new WebhookParam { Place = WebhookParamPlace.Body, Name = "h", Value = "hash" },
            ]
        };

        var notifier = Create([config], handler);
        await notifier.NotifyAsync(BatchWith([Torrent1]), CancellationToken.None);

        var (_, uri, _) = handler.Requests[0];
        Assert.Contains("cat=Movies", uri.Query);
    }

    [Fact]
    public async Task MultipleWebhooks_AllCalled()
    {
        var handler = new FakeHandler();
        var configs = new List<WebhookConfig>
        {
            new() { Url = "https://example.com/hook1" },
            new() { Url = "https://example.com/hook2" },
        };

        var notifier = Create(configs, handler);
        await notifier.NotifyAsync(BatchWith([Torrent1]), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FailingWebhook_DoesNotPreventOthers()
    {
        var handler = new FakeHandler { FailOnUrl = "https://example.com/hook1" };
        var configs = new List<WebhookConfig>
        {
            new() { Url = "https://example.com/hook1" },
            new() { Url = "https://example.com/hook2" },
        };

        var notifier = Create(configs, handler);
        await notifier.NotifyAsync(BatchWith([Torrent1]), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UnknownPropertyValue_ResolvesToEmpty()
    {
        var handler = new FakeHandler();
        var config = new WebhookConfig
        {
            Url = "https://example.com/hook",
            Params =
            [
                new WebhookParam { Place = WebhookParamPlace.Body, Name = "x", Value = "nonExistent" },
            ]
        };

        var notifier = Create([config], handler);
        await notifier.NotifyAsync(BatchWith([Torrent1]), CancellationToken.None);

        var json = JsonSerializer.Deserialize<JsonElement>(handler.Requests[0].Body!);
        Assert.Equal("", json.GetProperty("linked")[0].GetProperty("x").GetString());
    }

    [Fact]
    public async Task UrlWithExistingQueryString_AppendsParams()
    {
        var handler = new FakeHandler();
        var config = new WebhookConfig
        {
            Url = "https://example.com/hook?token=secret",
            Params =
            [
                new WebhookParam { Place = WebhookParamPlace.Query, Name = "h", Value = "hash" },
            ]
        };

        var notifier = Create([config], handler);
        await notifier.NotifyAsync(BatchWith([Torrent1]), CancellationToken.None);

        var (_, uri, _) = handler.Requests[0];
        Assert.Contains("token=secret", uri.Query);
        Assert.Contains("h=abc123", uri.Query);
    }

    internal sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = [];
        public string? FailOnUrl { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            Requests.Add((request.Method, request.RequestUri!, body));

            if (FailOnUrl is not null && request.RequestUri!.ToString().StartsWith(FailOnUrl))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
