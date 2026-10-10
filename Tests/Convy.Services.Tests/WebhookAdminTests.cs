using System.Net;
using System.Text.Json;
using Convy.Services.Rules;
using Convy.Services.Ui;
using Convy.Services.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public sealed class WebhookAdminTests : IDisposable
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public Exception? Failure { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Failure is not null)
                throw Failure;
            return new HttpResponseMessage(Status) { Content = new StringContent("{\"received\":true}") };
        }
    }

    private readonly TestDb _db = new();
    private readonly RecordingHandler _handler = new();
    private readonly List<WebhookConfig> _fileWebhooks = [new() { Name = "From file", Url = "http://file.example/hook" }];
    private readonly WebhookCatalog _catalog;
    private readonly WebhookAdminService _service;

    public WebhookAdminTests()
    {
        var store = new EfWebhookStore(_db, new FakeTime());
        _catalog = new WebhookCatalog(
            new WebhookConfigSource(new StaticOptions<List<WebhookConfig>>(_fileWebhooks), NullLogger<WebhookConfigSource>.Instance),
            store,
            NullLogger<WebhookCatalog>.Instance);
        var rules = new FakeRules
        {
            Yaml = """
                   rules:
                     - name: music
                       condition: "Category == Music"
                       path: /data/media/music
                   """,
        };
        _service = new WebhookAdminService(
            _catalog, store, new WebhookSender(new HttpClient(_handler), NullLogger<WebhookSender>.Instance), rules);
    }

    public void Dispose() => _db.Dispose();

    private static WebhookInput Input(string url = "https://n8n.example/hook", bool enabled = true, params WebhookParamDto[] parameters) => new()
    {
        Name = " Jobs to n8n ",
        Url = url,
        Events = ["job_status", "source_error"],
        Names = ["music", " "],
        Params = [.. parameters],
        Enabled = enabled,
    };

    [Fact]
    public async Task CreatedWebhooksAreStoredAndUsedNextToTheFileOnes()
    {
        var created = await _service.CreateAsync(Input(), CancellationToken.None);
        await _service.CreateAsync(Input("https://off.example/hook", enabled: false), CancellationToken.None);

        Assert.Equal(("ui", "Jobs to n8n", true), (created.Source, created.Name, created.Enabled));
        Assert.Equal(["job_status", "source_error"], created.Events);
        Assert.Equal(["music"], created.Names);

        Assert.Equal(["http://file.example/hook", "https://n8n.example/hook"], _catalog.Active.Select(w => w.Url));

        var list = _service.List();
        Assert.Equal(["file", "ui", "ui"], list.Webhooks.Select(w => w.Source));
        Assert.Null(list.Webhooks[0].Id);
        Assert.Equal(["linked"], list.Webhooks[0].Events);
        Assert.Equal(["music"], list.Rules);
        Assert.Contains("targetPath", list.Fields["linked"]);

        var updated = await _service.UpdateAsync(created.Id!.Value, Input("https://n8n.example/v2", enabled: false), CancellationToken.None);
        Assert.False(updated.Enabled);
        Assert.Equal(["http://file.example/hook"], _catalog.Active.Select(w => w.Url));

        await _service.DeleteAsync(created.Id.Value, CancellationToken.None);
        Assert.Single(_catalog.StoredWebhooks);
        await Assert.ThrowsAsync<ConvyRequestException>(() => _service.DeleteAsync(created.Id.Value, CancellationToken.None));
    }

    [Fact]
    public async Task ReloadingAfterARestartRestoresTheStoredWebhooks()
    {
        await _service.CreateAsync(Input(parameters: new WebhookParamDto("body", "id", "job_id")), CancellationToken.None);

        var restarted = new WebhookCatalog(
            new WebhookConfigSource(new StaticOptions<List<WebhookConfig>>([]), NullLogger<WebhookConfigSource>.Instance),
            new EfWebhookStore(_db, new FakeTime()),
            NullLogger<WebhookCatalog>.Instance);
        await restarted.ReloadAsync(CancellationToken.None);

        var param = Assert.Single(Assert.Single(restarted.Active).Params!);
        Assert.Equal((WebhookParamPlace.Body, "id", "job_id"), (param.Place, param.Name, param.Value));
    }

    [Theory]
    [InlineData("ftp://x/y", "url must be an absolute http(s) address.")]
    [InlineData("", "url must be an absolute http(s) address.")]
    public void RejectsBadUrls(string url, string message)
    {
        var ex = Assert.Throws<ConvyRequestException>(() => WebhookAdminService.Validate(new WebhookInput { Url = url }));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void RejectsUnknownEventsAndIncompleteParameters()
    {
        var ex = Assert.Throws<ConvyRequestException>(() => WebhookAdminService.Validate(new WebhookInput
        {
            Url = "https://x.example",
            Events = ["linked", "job_done"],
            Params = [new WebhookParamDto("header", "a", "b"), new WebhookParamDto("query", "", "job_id")],
        }));

        Assert.Contains("Unknown event 'job_done'", ex.Message);
        Assert.Contains("Parameter 1: place must be query or body.", ex.Message);
        Assert.Contains("Parameter 2 needs a name and a field.", ex.Message);
    }

    [Fact]
    public async Task TestSendsASampleEventWithTheParametersAndReportsTheAnswer()
    {
        var result = await _service.TestAsync(new WebhookTestRequest
        {
            Webhook = Input("https://n8n.example/hook?token=1",
                parameters: [new WebhookParamDto("query", "job", "job_id"), new WebhookParamDto("body", "state", "status")]),
            Event = "job_status",
        }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("https://n8n.example/hook?token=1&job=j_0", result.Url);
        Assert.Equal("{\"state\":\"completed\"}", result.RequestBody);
        Assert.Equal("{\"received\":true}", result.ResponseBody);
        Assert.Null(result.Error);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("true", Assert.Single(request.Headers.GetValues("X-Convy-Test")));
    }

    [Fact]
    public async Task TestOfTheLinkedEventSendsTheBatchShape()
    {
        var result = await _service.TestAsync(
            new WebhookTestRequest { Webhook = new WebhookInput { Url = "https://x.example/hook" }, Event = "linked" },
            CancellationToken.None);

        var body = JsonDocument.Parse(result.RequestBody).RootElement;
        Assert.Equal("Convy test - Evanescence - Fallen", body.GetProperty("linked")[0].GetProperty("name").GetString());
        Assert.Equal(0, body.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task TestReportsRefusalsAndUnreachableReceivers()
    {
        _handler.Status = HttpStatusCode.Unauthorized;
        var refused = await _service.TestAsync(
            new WebhookTestRequest { Webhook = new WebhookInput { Url = "https://x.example/hook" }, Event = "source_error" },
            CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal(401, refused.StatusCode);

        _handler.Failure = new HttpRequestException("Connection refused (x.example:443)");
        var down = await _service.TestAsync(
            new WebhookTestRequest { Webhook = new WebhookInput { Url = "https://x.example/hook" }, Event = "source_error" },
            CancellationToken.None);
        Assert.False(down.Ok);
        Assert.Null(down.StatusCode);
        Assert.Equal("Connection refused (x.example:443)", down.Error);

        await Assert.ThrowsAsync<ConvyRequestException>(() => _service.TestAsync(
            new WebhookTestRequest { Webhook = new WebhookInput { Url = "https://x.example/hook" }, Event = "job_done" },
            CancellationToken.None));
    }
}

public sealed class RulesViewServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"convy-rules-view-{Guid.NewGuid():N}.yaml");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public async Task ShowsTheFileAndTheRulesInEffect()
    {
        const string yaml = "rules:\n  - name: music\n    condition: \"Category == Music && Size > 0\"\n    path: /data/media/music\n";
        File.WriteAllText(_path, yaml);
        var provider = new RulesProvider(_path, NullLogger<RulesProvider>.Instance);

        var view = await new RulesViewService(provider, provider).GetAsync(CancellationToken.None);

        Assert.True(view.Exists);
        Assert.Equal(yaml, view.Text);
        Assert.Equal(1, view.Version);
        var rule = Assert.Single(view.Rules);
        Assert.Equal(("music", "Category == Music && Size > 0", "/data/media/music"), (rule.Name, rule.Condition, rule.Path));
        Assert.Equal(["Category", "Size"], rule.Properties);
        Assert.Null(view.Error);
    }

    [Fact]
    public async Task ABrokenFileIsShownWithItsErrorWhileThePreviousRulesStay()
    {
        File.WriteAllText(_path, "rules:\n  - condition: \"Size > 0\"\n    path: /data/a\n");
        File.SetLastWriteTimeUtc(_path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var provider = new RulesProvider(_path, NullLogger<RulesProvider>.Instance);
        var service = new RulesViewService(provider, provider);
        await service.GetAsync(CancellationToken.None);

        File.WriteAllText(_path, "rules:\n  - condition: \"Size >> 0\"\n    path: /data/b\n");
        File.SetLastWriteTimeUtc(_path, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var view = await service.GetAsync(CancellationToken.None);

        Assert.Contains("Size >> 0", view.Text);
        Assert.Equal("/data/a", Assert.Single(view.Rules).Path);
        Assert.NotNull(view.Error);
        Assert.NotNull(view.ErrorAt);
    }

    [Fact]
    public async Task AMissingFileHasNoTextAndNoRules()
    {
        var provider = new RulesProvider(_path, NullLogger<RulesProvider>.Instance);

        var view = await new RulesViewService(provider, provider).GetAsync(CancellationToken.None);

        Assert.False(view.Exists);
        Assert.Null(view.Text);
        Assert.Empty(view.Rules);
    }
}
