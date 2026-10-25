using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Convy.Services.Captcha;
using Convy.Services.External;
using Convy.Services.Plugins;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

/// <summary>
/// A fake captcha-solver: answers <c>/solve</c>, <c>/challenges/{id}</c> and <c>/fetch</c> with what the test queued and
/// records each call's path, key and JSON body.
/// </summary>
internal sealed class FakeCaptchaService : HttpMessageHandler
{
    public Queue<JsonObject> Solves { get; } = new();

    public Dictionary<Guid, JsonObject> Challenges { get; } = [];

    public Queue<JsonObject> Fetches { get; } = new();

    public List<(string Path, string? Key, JsonObject? Body)> Calls { get; } = [];

    public IEnumerable<JsonObject> Bodies(string path) => Calls.Where(c => c.Path == path).Select(c => c.Body!);

    /// <summary>A <c>/solve</c> answer: <paramref name="state"/> for a check on gallery.test.</summary>
    public static JsonObject Check(string state, Guid? id = null, string mode = "transplant", params (string Name, string Value)[] cookies) => new()
    {
        ["id"] = (id ?? Guid.NewGuid()).ToString(),
        ["state"] = state,
        ["domain"] = "gallery.test",
        ["profile"] = "gallery.test#convy",
        ["url"] = "https://gallery.test/api/g/g1",
        ["createdAt"] = "2026-10-07T12:00:00Z",
        ["expiresAt"] = "2026-10-07T12:30:00Z",
        ["userAgent"] = state == "solved" ? "Browser/1" : null,
        ["mode"] = state == "solved" ? mode : null,
        ["cookies"] = new JsonArray(cookies.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["value"] = c.Value, ["domain"] = "gallery.test", ["path"] = "/" }).ToArray()),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        lock (Calls) Calls.Add((path, request.Headers.Authorization?.Parameter, body));
        JsonObject? answer = path switch
        {
            "solve" => Solves.Dequeue(),
            "fetch" => Fetches.Dequeue(),
            _ when path.StartsWith("challenges/", StringComparison.Ordinal) => Challenges.GetValueOrDefault(Guid.Parse(path["challenges/".Length..])),
            _ => null,
        };
        return answer is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not found"}""", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}

public class CaptchaTests
{
    private static readonly PluginManifest Manifest = new() { Id = "test", Name = "Test", Domains = ["gallery.test"] };

    private static CaptchaService Service(FakeCaptchaService fake, FakeTime time, string? url = "http://captcha-solver:8191/") =>
        new(new StaticOptions<CaptchaOptions>(new CaptchaOptions { Url = url, Key = "convy-key" }), fake, time, NullLogger<CaptchaService>.Instance);

    /// <summary>Cloudflare's «Just a moment...» page: what a site behind a check answers.</summary>
    private static HttpResponseMessage CloudflarePage() => new(HttpStatusCode.Forbidden)
    {
        Headers = { { "Server", "cloudflare" } },
        Content = new StringContent("<html><head><title>Just a moment...</title></head><body><script>window._cf_chl_opt={}</script></body></html>", Encoding.UTF8, "text/html"),
    };

    private static HttpRequestMessage Get(string url, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return request;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join("; ", values) : null;

    [Fact]
    public async Task APassedCheckSendsTheRequestAgainWithTheBrowsersCookiesAndUserAgent()
    {
        var site = new FakeSiteHandler();
        var seen = new List<(string? Cookie, string? Agent)>();
        site.Answer("https://gallery.test/api/g/g1", request =>
        {
            seen.Add((Header(request, "Cookie"), Header(request, "User-Agent")));
            return Header(request, "Cookie")?.Contains("cf_clearance=ok", StringComparison.Ordinal) == true
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"g1"}""") }
                : CloudflarePage();
        });
        var fake = new FakeCaptchaService();
        fake.Solves.Enqueue(FakeCaptchaService.Check("solved", cookies: [("cf_clearance", "ok"), ("sid", "from-the-browser")]));
        using var captcha = Service(fake, new FakeTime());
        using var http = new PluginHttp(Manifest, site, () => TimeSpan.FromSeconds(30), new FakeTime(), captcha);

        using var response = await http.SendAsync(Get("https://gallery.test/api/g/g1", "sid=mine"), streaming: false, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var solve = Assert.Single(fake.Bodies("solve"));
        Assert.Equal("https://gallery.test/api/g/g1", (string?)solve["url"]);
        // Convy's own profile, with the plugin's own cookie (a sign-in) along: it never reaches the shared one.
        Assert.Equal("gallery.test#convy", (string?)solve["profile"]);
        Assert.Equal("sid", (string?)solve["cookies"]![0]!["name"]);
        Assert.Equal(25, (int)solve["wait"]!);
        Assert.Equal("convy-key", fake.Calls[0].Key);
        // The second request carries the check's cookie next to the plugin's own (not the browser's copy of it).
        Assert.Equal(("cf_clearance=ok; sid=mine", "Browser/1"), seen[1]);

        // Later requests carry them from the start: no new check.
        using var later = await http.SendAsync(Get("https://gallery.test/api/g/g1"), streaming: true, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.Single(fake.Bodies("solve"));
        Assert.Equal(("cf_clearance=ok", "Browser/1"), seen[2]);
    }

    [Fact]
    public async Task ASiteThatStillShowsTheCheckToTheCookiesIsAskedThroughTheServicesBrowser()
    {
        var site = new FakeSiteHandler();
        site.Answer("https://gallery.test/api/g/g1", _ => CloudflarePage());
        var fake = new FakeCaptchaService();
        fake.Solves.Enqueue(FakeCaptchaService.Check("solved", cookies: [("cf_clearance", "ok")]));
        fake.Solves.Enqueue(FakeCaptchaService.Check("solved", mode: "browser", cookies: [("cf_clearance", "ok2")]));
        for (var i = 0; i < 2; i++)
        {
            fake.Fetches.Enqueue(new JsonObject
            {
                ["status"] = 200,
                ["url"] = "https://gallery.test/api/g/g1",
                ["headers"] = new JsonObject { ["content-type"] = "application/json", ["content-encoding"] = "br" },
                ["body"] = Convert.ToBase64String("""{"id":"g1"}"""u8.ToArray()),
            });
        }

        using var captcha = Service(fake, new FakeTime());
        using var http = new PluginHttp(Manifest, site, () => TimeSpan.FromSeconds(30), new FakeTime(), captcha);

        using var response = await http.SendAsync(Get("https://gallery.test/api/g/g1", "sid=mine"), streaming: false, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"id":"g1"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal([false, true], fake.Bodies("solve").Select(b => (bool)b["afterTransplant"]!));
        var fetch = Assert.Single(fake.Bodies("fetch"));
        Assert.Equal("gallery.test#convy", (string?)fetch["profile"]);
        Assert.Equal("sid", (string?)fetch["cookies"]![0]!["name"]);
        Assert.Null(fetch["headers"]!["Cookie"]);

        // The domain stays with the browser: the site itself is not asked again.
        var asked = site.Requested.Count;
        using var later = await http.SendAsync(Get("https://gallery.test/api/g/g1"), streaming: false, CancellationToken.None);
        Assert.Equal(asked, site.Requested.Count);
        Assert.Equal(2, fake.Bodies("fetch").Count());
    }

    [Fact]
    public async Task ACheckThatWaitsForAPersonIsTheRequestsChallengeUntilAPersonPassesIt()
    {
        var site = new FakeSiteHandler();
        site.Answer("https://gallery.test/api/g/g1", request => Header(request, "Cookie") == "cf_clearance=human"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : CloudflarePage());
        var fake = new FakeCaptchaService();
        var id = Guid.NewGuid();
        fake.Solves.Enqueue(FakeCaptchaService.Check("pending", id));
        var time = new FakeTime();
        using var captcha = Service(fake, time);
        using var http = new PluginHttp(Manifest, site, () => TimeSpan.FromSeconds(30), time, captcha);

        var waiting = await Assert.ThrowsAsync<SourceException>(() => http.SendAsync(Get("https://gallery.test/api/g/g1"), false, CancellationToken.None));

        Assert.Equal(SourceErrorKind.Challenge, waiting.Kind);
        Assert.Equal(new Uri("https://gallery.test/api/g/g1"), waiting.ChallengeUrl);
        Assert.True(PluginHttp.Checked(waiting));
        Assert.Equal(id, captcha.WaitingFor("img.gallery.test")?.Id);

        // A person passed it in a client's window: the next request takes its cookies first.
        fake.Challenges[id] = FakeCaptchaService.Check("solved", id, cookies: [("cf_clearance", "human")]);
        time.Now += TimeSpan.FromSeconds(10);
        using var response = await http.SendAsync(Get("https://gallery.test/api/g/g1"), false, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(captcha.WaitingFor("gallery.test"));
        Assert.Single(fake.Bodies("solve"));
    }

    [Fact]
    public async Task WithoutAServiceACheckIsReportedAndOtherErrorsAreReturnedAsTheyAre()
    {
        var site = new FakeSiteHandler();
        site.Answer("https://gallery.test/check", _ => CloudflarePage());
        site.Answer("https://gallery.test/denied", _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Headers = { { "Server", "cloudflare" } },
            Content = new StringContent("<title>Access denied</title>"),
        });
        var fake = new FakeCaptchaService();
        using var captcha = Service(fake, new FakeTime(), url: null);
        using var http = new PluginHttp(Manifest, site, () => TimeSpan.FromSeconds(30), new FakeTime(), captcha);

        var check = await Assert.ThrowsAsync<SourceException>(() => http.SendAsync(Get("https://gallery.test/check"), true, CancellationToken.None));
        Assert.Equal(SourceErrorKind.Challenge, check.Kind);
        Assert.Contains("no service of checks is set up", check.Message);

        // A block page of the same server is the site's answer, readable by the plugin.
        using var denied = await http.SendAsync(Get("https://gallery.test/denied"), true, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("Access denied", await denied.Content.ReadAsStringAsync());
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task TheRestApiNamesTheCheckThatWaitsForAPersonAndWaitsOnlyAsLongAsItsTimeAllows()
    {
        var fake = new FakeCaptchaService();
        var id = Guid.NewGuid();
        fake.Solves.Enqueue(FakeCaptchaService.Check("pending", id));
        using var captcha = Service(fake, new FakeTime());
        using var h = new GalleryHarness(captcha);
        h.Web.Answer("https://gallery.test/api/g/g1", _ => CloudflarePage());

        var error = await Assert.ThrowsAsync<ApiException>(() => h.Api.ResolveAsync("test-gallery", "https://gallery.test/g/g1", CancellationToken.None));

        Assert.Equal(403, error.Status);
        Assert.Equal(id, error.ChallengeId);
        Assert.Equal(new Uri("https://gallery.test/api/g/g1"), error.ChallengeUrl);
        // The source has 5 s: the check is waited for only until the answer must go.
        Assert.InRange((int)fake.Bodies("solve").Single()["wait"]!, 0, 1);
    }

    [Fact]
    public async Task ASitesOwnCaptchaThePluginReportsIsHandedToTheServiceForAPerson()
    {
        var fake = new FakeCaptchaService();
        var id = Guid.NewGuid();
        fake.Solves.Enqueue(FakeCaptchaService.Check("pending", id));
        using var captcha = Service(fake, new FakeTime());
        using var h = new GalleryHarness(captcha);
        h.Web.Answer("https://gallery.test/api/g/g1", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { { "X-Captcha", "https://gallery.test/captcha" } },
            Content = new StringContent("{}"),
        });

        var error = await Assert.ThrowsAsync<ApiException>(() => h.Api.ResolveAsync("test-gallery", "https://gallery.test/g/g1", CancellationToken.None));

        Assert.Equal(403, error.Status);
        Assert.Equal(id, error.ChallengeId);
        var solve = Assert.Single(fake.Bodies("solve"));
        Assert.Equal("site", (string?)solve["kind"]);
        Assert.Equal("https://gallery.test/captcha", (string?)solve["url"]);
    }

    [Fact]
    public void TheDetectorTellsAChecksPageFromABlockPage()
    {
        string Server(string name) => name == "server" ? "cloudflare" : "";

        Assert.True(ChallengeDetector.IsChallenge(503, Server, "<title>Just a moment...</title>"));
        Assert.True(ChallengeDetector.IsChallenge(200, n => n == "cf-mitigated" ? "challenge" : "", null));
        Assert.False(ChallengeDetector.IsChallenge(403, Server, "<title>Access denied</title>"));
        Assert.False(ChallengeDetector.IsChallenge(403, _ => "nginx", "<title>Just a moment...</title>"));
        Assert.False(ChallengeDetector.MayBe(404, Server));
        Assert.True(ChallengeDetector.IsChallenge(403, n => n == "server" ? "ddos-guard" : "", "<script src=\"/.well-known/ddos-guard/js-challenge/index.js\">"));
    }
}
