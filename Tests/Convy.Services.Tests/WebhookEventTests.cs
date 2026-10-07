using System.Net;
using System.Text.Json;
using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Media;
using Convy.Services.Webhooks;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class WebhookEventDispatcherTests
{
    private sealed class FlakyHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];
        public Dictionary<string, int> FailuresLeft { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request.RequestUri!, body));

            var host = request.RequestUri!.Host;
            lock (FailuresLeft)
            {
                if (FailuresLeft.TryGetValue(host, out var left) && left > 0)
                {
                    FailuresLeft[host] = left - 1;
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static WebhookEventDispatcher Create(IReadOnlyList<WebhookConfig> webhooks, FlakyHandler handler, int maxRetries = 5) =>
        new(() => webhooks,
            new WebhookSender(new HttpClient(handler), NullLogger<WebhookSender>.Instance),
            new WebhookDeliveryOptions { MaxRetries = maxRetries, FirstRetryDelay = TimeSpan.FromMilliseconds(1) },
            TimeProvider.System,
            NullLogger<WebhookEventDispatcher>.Instance);

    private static WebhookEvent JobEvent(string? rule) => new(WebhookEvents.JobStatus, rule,
        new Dictionary<string, object?> { ["event"] = "job_status", ["job_id"] = "j_1", ["status"] = "completed", ["files"] = new[] { "a", "b" } });

    [Fact]
    public async Task OnlySubscribedWebhooksReceiveEvents()
    {
        var handler = new FlakyHandler();
        var dispatcher = Create(
        [
            new WebhookConfig { Url = "http://legacy/" },                                        // linked only
            new WebhookConfig { Url = "http://jobs/", Events = ["job_status", "source_error"] },
            new WebhookConfig { Url = "http://linked/", Events = ["linked"] },
        ], handler);

        await dispatcher.DeliverAsync(JobEvent("music"), CancellationToken.None);

        Assert.Equal("jobs", Assert.Single(handler.Requests).Uri.Host);
    }

    [Fact]
    public async Task NamesFilterJobEventsByRule()
    {
        var handler = new FlakyHandler();
        var dispatcher = Create(
        [
            new WebhookConfig { Url = "http://music/", Events = ["job_status"], Names = ["music"] },
            new WebhookConfig { Url = "http://all/", Events = ["job_status"] },
        ], handler);

        await dispatcher.DeliverAsync(JobEvent("movies"), CancellationToken.None);
        await dispatcher.DeliverAsync(JobEvent(null), CancellationToken.None);
        await dispatcher.DeliverAsync(JobEvent("music"), CancellationToken.None);

        Assert.Equal(["all", "all", "all", "music"], handler.Requests.Select(r => r.Uri.Host).Order());
    }

    [Fact]
    public async Task SourceErrorsIgnoreTheNamesFilter()
    {
        var handler = new FlakyHandler();
        var dispatcher = Create([new WebhookConfig { Url = "http://ops/", Events = ["source_error"], Names = ["music"] }], handler);

        await dispatcher.DeliverAsync(new WebhookEvent(WebhookEvents.SourceError, null, new Dictionary<string, object?>()), CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FailedDeliveryIsRetriedUntilAccepted()
    {
        var handler = new FlakyHandler { FailuresLeft = { ["jobs"] = 3 } };
        var dispatcher = Create([new WebhookConfig { Url = "http://jobs/", Events = ["job_status"] }], handler);

        await dispatcher.DeliverAsync(JobEvent(null), CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task GivesUpAfterTheMaximumRetries()
    {
        var handler = new FlakyHandler { FailuresLeft = { ["jobs"] = 100 } };
        var dispatcher = Create([new WebhookConfig { Url = "http://jobs/", Events = ["job_status"] }], handler, maxRetries: 5);

        await dispatcher.DeliverAsync(JobEvent(null), CancellationToken.None);

        Assert.Equal(6, handler.Requests.Count); // first attempt + 5 retries
    }

    [Fact]
    public async Task ParamsSelectEventFields()
    {
        var handler = new FlakyHandler();
        var dispatcher = Create([new WebhookConfig
        {
            Url = "http://n8n/hook?x=1",
            Events = ["job_status"],
            Params =
            [
                new WebhookParam { Place = WebhookParamPlace.Body, Name = "id", Value = "job_id" },
                new WebhookParam { Place = WebhookParamPlace.Query, Name = "s", Value = "status" },
                new WebhookParam { Place = WebhookParamPlace.Query, Name = "f", Value = "files" },
            ],
        }], handler);

        await dispatcher.DeliverAsync(JobEvent(null), CancellationToken.None);

        var (uri, body) = Assert.Single(handler.Requests);
        Assert.Equal("?x=1&s=completed&f=a%2Cb", uri.Query);
        Assert.Equal("""{"id":"j_1"}""", body);
    }

    [Fact]
    public async Task RunDeliversQueuedEventsInOrder()
    {
        var handler = new FlakyHandler();
        var dispatcher = Create([new WebhookConfig { Url = "http://jobs/", Events = ["job_status"] }], handler);
        using var stop = new CancellationTokenSource();

        var run = dispatcher.RunAsync(stop.Token);
        for (var i = 0; i < 3; i++)
        {
            dispatcher.Enqueue(new WebhookEvent(WebhookEvents.JobStatus, null, new Dictionary<string, object?> { ["n"] = i }));
        }

        while (handler.Requests.Count < 3)
        {
            await Task.Delay(10);
        }

        await stop.CancelAsync();
        await run;

        Assert.Equal(["""{"n":0}""", """{"n":1}""", """{"n":2}"""], handler.Requests.Select(r => r.Body));
    }

    [Fact]
    public void EventsWithoutSubscribersAreNotQueued()
    {
        var dispatcher = Create([new WebhookConfig { Url = "http://legacy/" }], new FlakyHandler());

        dispatcher.Enqueue(JobEvent(null)); // nobody listens; must neither throw nor block
    }
}

public class WebhookConfigSourceTests
{
    private sealed class BrokenMonitor : Microsoft.Extensions.Options.IOptionsMonitor<List<WebhookConfig>>
    {
        public bool Broken { get; set; }

        public List<WebhookConfig> CurrentValue => Broken
            ? throw new InvalidOperationException("Failed to convert configuration value 'header' to type 'WebhookParamPlace'.")
            : [new WebhookConfig { Url = "http://ok/" }];

        public List<WebhookConfig> Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<List<WebhookConfig>, string?> listener) => null;
    }

    [Fact]
    public void InvalidConfigurationKeepsTheLastGoodOne()
    {
        var monitor = new BrokenMonitor();
        var source = new WebhookConfigSource(monitor, NullLogger<WebhookConfigSource>.Instance);

        Assert.Single(source.Current);
        monitor.Broken = true;
        Assert.Equal("http://ok/", Assert.Single(source.Current).Url);
    }

    [Fact]
    public void EnqueueNeverThrows()
    {
        var dispatcher = new WebhookEventDispatcher(
            () => throw new InvalidOperationException("bad config"),
            new WebhookSender(new HttpClient(), NullLogger<WebhookSender>.Instance),
            new WebhookDeliveryOptions(), TimeProvider.System, NullLogger<WebhookEventDispatcher>.Instance);

        dispatcher.Enqueue(new WebhookEvent(WebhookEvents.JobStatus, null, new Dictionary<string, object?>()));
    }
}

public class JobStatusEventTests
{
    [Fact]
    public void BodyMatchesTheContract()
    {
        var job = new JobRecord
        {
            Id = 42,
            Provider = DownloadProviders.Slskd,
            ItemRef = "bob/album",
            Category = "music",
            ClientCategory = "Music",
            Title = "Evanescence - Fallen",
            Status = JobStatus.Completed,
            Rule = "music",
            TargetPath = "/data/media/music/Evanescence/2003 - Fallen",
            SizeBytes = 432000000,
            FileCount = 250,
        };
        var placed = Enumerable.Range(1, 250).Select(i => $"{i:000}.flac").ToList();

        var webhookEvent = WebhookJobEvents.ToEvent(new JobStatusChange(job, JobStatus.Placing, placed));
        var json = JsonSerializer.SerializeToElement(webhookEvent.Payload);

        Assert.Equal("job_status", webhookEvent.Name);
        Assert.Equal("music", webhookEvent.RuleName);
        Assert.Equal("job_status", json.GetProperty("event").GetString());
        Assert.Equal("j_42", json.GetProperty("job_id").GetString());
        Assert.Equal("completed", json.GetProperty("status").GetString());
        Assert.Equal("placing", json.GetProperty("previous_status").GetString());
        Assert.Equal("slskd", json.GetProperty("provider").GetString());
        Assert.Equal("music", json.GetProperty("category").GetString());
        Assert.Equal("/data/media/music/Evanescence/2003 - Fallen", json.GetProperty("path").GetString());
        Assert.Equal(200, json.GetProperty("files").GetArrayLength());
        Assert.Equal(250, json.GetProperty("files_total").GetInt32());
        Assert.Equal(432000000, json.GetProperty("size_bytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("error").ValueKind);
    }

    [Fact]
    public void NewJobHasNoPreviousStatus()
    {
        var job = new JobRecord
        {
            Id = 1, Provider = "qbittorrent", ItemRef = "h", Category = "movies", Title = "M", Status = JobStatus.Queued, FileCount = 3,
        };

        var json = JsonSerializer.SerializeToElement(WebhookJobEvents.ToEvent(new JobStatusChange(job, null)).Payload);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("previous_status").ValueKind);
        Assert.Equal(0, json.GetProperty("files").GetArrayLength());
        Assert.Equal(3, json.GetProperty("files_total").GetInt32());
    }
}

public class SourceHealthMonitorTests
{
    [Fact]
    public void RaisesSourceErrorOnTransitionsIntoFailureOnly()
    {
        var queue = new RecordingQueue();
        var monitor = Media.Health(queue);

        monitor.Report("prowlarr:12", "ok", null);
        monitor.Report("prowlarr:12", "error", "Indexer is failing");
        monitor.Report("prowlarr:12", "error", "still failing");         // no repeat
        monitor.Report("prowlarr:12", "timeout", null);                  // says nothing
        monitor.Report("prowlarr:12", "auth_failed", "cookies expired"); // different failure
        monitor.Report("prowlarr:12", "empty", null);
        monitor.Report("prowlarr:12", "error", "again");

        Assert.Equal(["error", "auth_failed", "error"], queue.Events.Select(e => (string)e.Payload["status"]!));
        var first = queue.Events[0];
        Assert.Equal("source_error", first.Name);
        Assert.Equal("prowlarr:12", first.Payload["source"]);
        Assert.Equal("Indexer is failing", first.Payload["message"]);
    }

    [Fact]
    public async Task SearchReportsFailingSources()
    {
        using var db = new TestDb();
        var time = new FakeTime();
        var queue = new RecordingQueue();
        var failing = new FakeSource("prowlarr:1") { Failure = new SourceException(SourceErrorKind.AuthFailed, "Prowlarr rejected the API key.") };
        var health = Media.Health(queue);
        var service = new SearchService(
            Media.Catalog(Media.Categories("prowlarr:1")),
            new SourceRegistry([new StaticProvider(failing)], health, time, NullLogger<SourceRegistry>.Instance),
            new EfSearchCache(db, time),
            health,
            new StaticOptions<SearchOptions>(new SearchOptions { SourceTimeoutSeconds = 1 }),
            time,
            NullLogger<SearchService>.Instance);

        await service.StartAsync("music", ["q"], null, CancellationToken.None);

        var sourceError = Assert.Single(queue.Events);
        Assert.Equal("auth_failed", sourceError.Payload["status"]);
    }

    [Fact]
    public async Task RegistryReportsAFailingProviderBeforeAnySourceIsKnown()
    {
        var queue = new RecordingQueue();
        var provider = new StaticProvider { Failure = new SourceException(SourceErrorKind.AuthFailed, "Prowlarr rejected the API key.") };
        var registry = new SourceRegistry([provider], Media.Health(queue), new FakeTime(), NullLogger<SourceRegistry>.Instance);

        await registry.GetSourcesAsync(CancellationToken.None);

        var sourceError = Assert.Single(queue.Events);
        Assert.Equal("static", sourceError.Payload["source"]);
        Assert.Equal("auth_failed", sourceError.Payload["status"]);
    }
}
