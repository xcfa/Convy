using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Convy.Services.Captcha;
using Convy.Services.Extensions;
using Convy.Services.External;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

/// <summary>Answers requests by absolute URL (query included); anything else is a 404. Records what was asked.</summary>
internal sealed class FakeSiteHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<string> Requested { get; } = [];

    /// <summary>The requests as sent, headers included.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    public void Json(string url, string json) =>
        _routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public void Bytes(string url, byte[] bytes, string mediaType = "image/jpeg") =>
        _routes[url] = _ =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

    public void Status(string url, HttpStatusCode status) => _routes[url] = _ => new HttpResponseMessage(status);

    public void Answer(string url, Func<HttpRequestMessage, HttpResponseMessage> answer) => _routes[url] = answer;

    /// <summary>Cloudflare's «Just a moment...» page: what a site behind a check answers.</summary>
    public static HttpResponseMessage CloudflarePage() => new(HttpStatusCode.Forbidden)
    {
        Headers = { { "Server", "cloudflare" } },
        Content = new StringContent("<html><head><title>Just a moment...</title></head><body><script>window._cf_chl_opt={}</script></body></html>", Encoding.UTF8, "text/html"),
    };

    public static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join("; ", values) : null;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.AbsoluteUri;
        lock (Requested)
        {
            Requested.Add(key);
            Requests.Add(request);
        }

        var response = _routes.TryGetValue(key, out var route) ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}

/// <summary>
/// A fake captcha-solver: answers <c>/solve</c>, <c>/challenges/{id}</c> and <c>/fetch</c> with what the test queued and
/// records each call's path, key and JSON body.
/// </summary>
internal sealed class FakeCaptchaService : HttpMessageHandler
{
    public Queue<JsonObject> Solves { get; } = new();

    public Dictionary<Guid, JsonObject> Challenges { get; } = [];

    public List<(string Path, string? Key, JsonObject? Body)> Calls { get; } = [];

    public IEnumerable<JsonObject> Bodies(string path) => Calls.Where(c => c.Path == path).Select(c => c.Body!);

    /// <summary>A <c>/solve</c> answer: <paramref name="state"/> for a check on gallery.test.</summary>
    public static JsonObject Check(string state, Guid? id = null, string mode = "transplant", params (string Name, string Value)[] cookies) => new()
    {
        ["id"] = (id ?? Guid.NewGuid()).ToString(),
        ["state"] = state,
        ["domain"] = "gallery.test",
        ["profile"] = "gallery.test#convy.test-gallery",
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
        lock (Calls)
        {
            Calls.Add((path, request.Headers.Authorization?.Parameter, body));
        }

        JsonObject? answer = path switch
        {
            "solve" => Solves.Dequeue(),
            _ when path.StartsWith("challenges/", StringComparison.Ordinal) => Challenges.GetValueOrDefault(Guid.Parse(path["challenges/".Length..])),
            _ => null,
        };
        return answer is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not found"}""", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}

internal static class Extensions
{
    public static string TestExtensionPath => Path.Combine(AppContext.BaseDirectory, "extensions", "Convy.Tests.TestExtension.dll");

    /// <summary>
    /// A host over a temporary extensions folder holding the test extension, with <paramref name="web"/> as its sites and
    /// <paramref name="captcha"/> as the service of checks (set up only when given).
    /// </summary>
    public static ExtensionHost Host(FakeSiteHandler web, ExtensionsOptions? options = null, FakeTime? time = null, FakeCaptchaService? captcha = null)
    {
        var folder = Directory.CreateTempSubdirectory("convy-extensions-").FullName;
        File.Copy(TestExtensionPath, Path.Combine(folder, "Convy.Tests.TestExtension.dll"));
        options ??= new ExtensionsOptions();
        options.Path = folder;
        options.Settings.TryAdd("test-gallery", new ExtensionEntryOptions { Values = { ["token"] = "secret" } });
        var captchaOptions = captcha is null ? new CaptchaOptions() : new CaptchaOptions { Url = "http://captcha-solver:8191/", Key = "convy-key" };
        var host = new ExtensionHost(
            new StaticOptions<ExtensionsOptions>(options), new StaticOptions<CaptchaOptions>(captchaOptions), time ?? new FakeTime(), NullLoggerFactory.Instance,
            _ => web, captcha ?? new FakeCaptchaService());
        host.LoadAll();
        return host;
    }

    public static string ContentExtensionPath => Path.Combine(AppContext.BaseDirectory, "extensions", "Convy.Tests.ContentExtension.dll");

    /// <summary>
    /// A host over a temporary extensions folder holding the test extension of Convy's own API (a book catalog, a
    /// service of indexers at <c>http://indexers.test</c>, peers), with <paramref name="web"/> as its sites.
    /// </summary>
    public static ExtensionHost ContentHost(FakeSiteHandler web, ExtensionsOptions? options = null, Convy.Sources.Torrents.ITorrentMetadataService? torrents = null)
    {
        var folder = Directory.CreateTempSubdirectory("convy-extensions-").FullName;
        File.Copy(ContentExtensionPath, Path.Combine(folder, "Convy.Tests.ContentExtension.dll"));
        options ??= new ExtensionsOptions();
        options.Path = folder;
        options.Settings.TryAdd("test-books", new ExtensionEntryOptions { Values = { ["token"] = "secret" } });
        options.Settings.TryAdd("test-indexers", new ExtensionEntryOptions { Values = { ["url"] = "http://indexers.test", ["key"] = "indexer-key" } });
        options.Settings.TryAdd("test-peers", new ExtensionEntryOptions { Values = { ["url"] = "http://peers.test" } });
        var host = new ExtensionHost(
            new StaticOptions<ExtensionsOptions>(options), new StaticOptions<CaptchaOptions>(new CaptchaOptions()), new FakeTime(), NullLoggerFactory.Instance,
            _ => web, new FakeCaptchaService(), torrents);
        host.LoadAll();
        return host;
    }

    public static async Task<ExtensionSource> GallerySource(ExtensionHost host) =>
        Assert.IsType<ExtensionSource>(Assert.Single(await host.GetSourcesAsync(CancellationToken.None)));
}

public class ExtensionHostTests
{
    [Fact]
    public async Task LoadsTheFolderAndServesTheSourcesThatDownload()
    {
        using var host = Extensions.Host(new FakeSiteHandler());

        Assert.Empty(host.Problems);
        var extension = Assert.Single(host.Extensions);
        Assert.Equal("Convy.Tests.TestExtension", extension.Package.Package);
        // The catalog only knows metadata: nothing for Convy to download from it.
        var source = await Extensions.GallerySource(host);
        Assert.Equal(("test-gallery", "Test gallery", Protocol.Gallery, SourceStatus.Ok), (source.Id, source.Name, source.Protocol, source.Status));
        Assert.Equal(["gallery.test"], source.Manifest.Domains);
        Assert.Equal((true, 18, true), (source.Manifest.Nsfw, source.Manifest.DefaultAgeRating, source.Manifest.CanSearch));
        var filter = Assert.Single(source.Manifest.Filters);
        Assert.Equal("category", filter.Key);
        Assert.Equal(["manga", "doujinshi"], filter.Options.Select(o => o.Value));
        Assert.Equal(["gallery.test"], host.Domains);
    }

    [Fact]
    public async Task SettingsAreReadLiveAndADisabledSourceListsNothing()
    {
        var web = new FakeSiteHandler();
        web.Json("https://gallery.test/api/g/g1", """{ "title": "One" }""");
        var options = new ExtensionsOptions();
        options.Settings["test-gallery"] = new ExtensionEntryOptions { Values = { ["token"] = "abc", ["cookies"] = "sid=1", ["nonsense"] = "x" } };
        using var host = Extensions.Host(web, options);
        var source = await Extensions.GallerySource(host);

        await source.ResolveUrlAsync(new Uri("https://gallery.test/g/g1"), CancellationToken.None);
        options.Settings["test-gallery"].Values["token"] = "def";
        await source.ResolveUrlAsync(new Uri("https://gallery.test/g/g1"), CancellationToken.None);

        Assert.Equal(["abc", "def"], web.Requests.Select(r => FakeSiteHandler.Header(r, "X-Token")));
        Assert.All(web.Requests, r => Assert.Contains("sid=1", FakeSiteHandler.Header(r, "Cookie")));
        Assert.All(web.Requests, r => Assert.StartsWith("Convy/", FakeSiteHandler.Header(r, "User-Agent")));

        options.Settings["test-gallery"].Enabled = false;
        Assert.Empty(await host.GetSourcesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheSourcesHttpStaysWithinTheManifestsDomains()
    {
        var web = new FakeSiteHandler();
        using var host = Extensions.Host(web);
        var source = await Extensions.GallerySource(host);

        Assert.Null(await source.ResolveUrlAsync(new Uri("https://elsewhere.test/g/g1"), CancellationToken.None));

        // An image outside the domains is refused by the host, not sent.
        await Assert.ThrowsAsync<SourceException>(() => source.OpenImageAsync(new Uri("https://evil.test/x.jpg"), CancellationToken.None));
        Assert.DoesNotContain("https://evil.test/x.jpg", web.Requested);

        // A redirect that leaves the domains is refused before the hop is sent.
        web.Answer("https://img.gallery.test/leave.jpg", _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://cdn.other.test/x.jpg") } });
        await Assert.ThrowsAsync<SourceException>(() => source.OpenImageAsync(new Uri("https://img.gallery.test/leave.jpg"), CancellationToken.None));
        Assert.DoesNotContain("https://cdn.other.test/x.jpg", web.Requested);

        // A subdomain is the site's; a redirect within the domains is followed.
        web.Answer("https://img.gallery.test/moved.jpg", _ => new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("/final.jpg", UriKind.Relative) } });
        web.Bytes("https://img.gallery.test/final.jpg", [9], "image/png");
        using var moved = await source.OpenImageAsync(new Uri("https://img.gallery.test/moved.jpg"), CancellationToken.None);
        Assert.Equal(("image/png", 1L), (moved.MediaType, moved.Length));
    }

    [Fact]
    public void AFolderWithABrokenPackageReportsItAndLoadsTheRest()
    {
        var folder = Directory.CreateTempSubdirectory("convy-extensions-").FullName;
        File.Copy(Extensions.TestExtensionPath, Path.Combine(folder, "good.dll"));
        File.WriteAllText(Path.Combine(folder, "broken.dll"), "not a dll");
        File.Copy(typeof(Convy.Sources.Torrents.MagnetUri).Assembly.Location, Path.Combine(folder, "not-an-extension.dll"));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a package either, and not looked at");
        using var host = new ExtensionHost(
            new StaticOptions<ExtensionsOptions>(new ExtensionsOptions { Path = folder }), new StaticOptions<CaptchaOptions>(new CaptchaOptions()),
            new FakeTime(), NullLoggerFactory.Instance, _ => new FakeSiteHandler(), new FakeCaptchaService());

        host.LoadAll();

        Assert.Single(host.Extensions);
        Assert.Equal(["broken.dll", "not-an-extension.dll"], host.Problems.Select(p => p.File).Order());
        Assert.All(host.Problems, p => Assert.False(string.IsNullOrWhiteSpace(p.Message)));
    }

    [Fact]
    public void AnInstallCutShortIsUndoneAtTheNextStart()
    {
        var folder = Directory.CreateTempSubdirectory("convy-extensions-").FullName;
        // Set aside for an update that never finished, and the staged update itself.
        File.Copy(Extensions.TestExtensionPath, Path.Combine(folder, "Convy.Tests.TestExtension.dll.old"));
        File.WriteAllText(Path.Combine(folder, "Convy.Tests.TestExtension.zip.new"), "half written");
        using var host = new ExtensionHost(
            new StaticOptions<ExtensionsOptions>(new ExtensionsOptions { Path = folder }), new StaticOptions<CaptchaOptions>(new CaptchaOptions()),
            new FakeTime(), NullLoggerFactory.Instance, _ => new FakeSiteHandler(), new FakeCaptchaService());

        host.LoadAll();

        Assert.Equal(["Convy.Tests.TestExtension"], host.Extensions.Select(e => e.Package.Package));
        Assert.Equal(["Convy.Tests.TestExtension.dll"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Empty(host.Problems);
    }

    [Fact]
    public void NoFolderIsNoExtensions()
    {
        using var host = new ExtensionHost(
            new StaticOptions<ExtensionsOptions>(new ExtensionsOptions { Path = Path.Combine(Path.GetTempPath(), "convy-none-" + Guid.NewGuid()) }),
            new StaticOptions<CaptchaOptions>(new CaptchaOptions()), new FakeTime(), NullLoggerFactory.Instance, _ => new FakeSiteHandler(), new FakeCaptchaService());

        host.LoadAll();

        Assert.Empty(host.Extensions);
        Assert.Empty(host.Problems);
    }
}

public class ExtensionSourceTests
{
    private static void Series(FakeSiteHandler web, string id, string title, int chapters)
    {
        web.Json($"https://gallery.test/api/s/{id}", $$"""{ "title": "{{title}}" }""");
        web.Json($"https://gallery.test/api/s/{id}/chapters",
            "[" + string.Join(",", Enumerable.Range(1, chapters).Select(n => $$"""{ "id": "{{id}}c{{n}}", "title": "{{title}} {{n}}" }""")) + "]");
    }

    [Fact]
    public async Task AReleaseIsItsAddressWithWhatTheSiteSaysAboutIt()
    {
        var web = new FakeSiteHandler();
        web.Json("https://gallery.test/api/g/g1",
            """{ "title": "One", "language": "en", "pages": 3, "chapter": "3", "series": "Saga", "tags": ["female:glasses", "full color"], "artists": ["Someone"], "circles": ["Ink Circle"] }""");
        using var host = Extensions.Host(web);
        var source = await Extensions.GallerySource(host);

        var found = (await source.ResolveUrlAsync(new Uri("https://gallery.test/g/g1"), CancellationToken.None))!;

        Assert.Equal(("https://gallery.test/g/g1", "One — Ch. 3", 3, "test-gallery:g1"), (found.ContentId, found.Title, found.FileCount, found.DedupKey));
        Assert.Equal("female:glasses, full color", found.Properties[GalleryProperties.Tags]);
        Assert.Equal(("Someone", "Ink Circle", "Saga", "en", "true"), (found.Properties[GalleryProperties.Artists], found.Properties[GalleryProperties.Groups],
            found.Properties[GalleryProperties.Series], found.Properties[GalleryProperties.Language], found.Properties[GalleryProperties.Nsfw]));

        var payload = Assert.IsType<GalleryPayload>(await source.ResolveAsync(found.ContentId, CancellationToken.None));
        Assert.Equal(("test-gallery", "https://gallery.test/g/g1", "One — Ch. 3", 3), (payload.SourceId, payload.ContentId, payload.Title, payload.PageCount));
        Assert.Contains("not the address of a release", (await Assert.ThrowsAsync<SourceException>(() => source.ResolveAsync("g1", CancellationToken.None))).Message);
    }

    [Fact]
    public async Task ASeriesIsNoReleaseItsChaptersAreAPageAtATime()
    {
        var web = new FakeSiteHandler();
        Series(web, "s1", "Saga", 150);
        web.Json("https://gallery.test/api/search?q=saga&page=1",
            """{ "items": [ { "id": "s2", "title": "Saga Gaiden", "series": true }, { "id": "s1", "title": "Saga", "series": true }, { "id": "g9", "title": "Saga fan book" } ] }""");
        web.Json("https://gallery.test/api/search?q=&page=1",
            """{ "items": [ { "id": "s1", "title": "Saga", "series": true }, { "id": "g9", "title": "Saga fan book" } ], "next": "2" }""");
        using var host = Extensions.Host(web);
        var source = await Extensions.GallerySource(host);

        // A text search: the chapters of the series whose title it is, a hundred at a time.
        var first = await source.BrowseAsync("saga", GallerySearch.Empty, CancellationToken.None);
        Assert.Equal(ExtensionSource.ChaptersPerPage, first.Items.Count);
        Assert.Equal(("https://gallery.test/g/s1c1", "Saga 1"), (first.Items[0].ContentId, first.Items[0].Title));
        Assert.Equal("c:100:https://gallery.test/s/s1", first.NextPage);
        var second = await source.BrowseAsync("saga", GallerySearch.Empty with { Page = first.NextPage }, CancellationToken.None);
        Assert.Equal((50, "Saga 101", null), (second.Items.Count, second.Items[0].Title, second.NextPage));

        // The series' link: its chapters too; resolved as a release, it is none.
        var linked = await source.BrowseAsync("https://gallery.test/s/s1", GallerySearch.Empty, CancellationToken.None);
        Assert.Equal(ExtensionSource.ChaptersPerPage, linked.Items.Count);
        Assert.Null(await source.ResolveUrlAsync(new Uri("https://gallery.test/s/s1"), CancellationToken.None));

        // The front page: its releases, the series left out, the site's page token kept.
        var front = await source.BrowseAsync(null, GallerySearch.Empty, CancellationToken.None);
        Assert.Equal(("https://gallery.test/g/g9", "b:2"), (Assert.Single(front.Items).ContentId, front.NextPage));
    }

    [Fact]
    public async Task ThePagesTheListingNamesAreTakenAsTheyAreTheOthersAskedFor()
    {
        var web = new FakeSiteHandler();
        web.Json("https://gallery.test/api/g/g1/pages", """[ { "image": "https://img.gallery.test/img/g1/1.jpg" }, {} ]""");
        web.Json("https://gallery.test/api/g/g1/2", """{ "image": "https://img.gallery.test/broken/g1/2.jpg" }""");
        using var host = Extensions.Host(web);
        var source = await Extensions.GallerySource(host);

        var pages = await source.GetPagesAsync("https://gallery.test/g/g1", CancellationToken.None);
        Assert.Equal(["1.jpg", "2"], (await source.ListFilesAsync("https://gallery.test/g/g1", CancellationToken.None)).Files.Select(f => f.Path));

        Assert.Equal("https://img.gallery.test/img/g1/1.jpg", (await source.GetImageUrlAsync(pages[0], CancellationToken.None)).AbsoluteUri);
        Assert.Null(await source.RetryImageUrlAsync(pages[0], pages[0].Url, CancellationToken.None));
        var image = await source.GetImageUrlAsync(pages[1], CancellationToken.None);
        Assert.Equal("https://img.gallery.test/broken/g1/2.jpg", image.AbsoluteUri);
        Assert.Equal("https://img.gallery.test/img/g1/2.jpg", (await source.RetryImageUrlAsync(pages[1], image, CancellationToken.None))!.AbsoluteUri);
    }

    [Fact]
    public async Task TheSitesAnswersBecomeErrorsOfTheirKind()
    {
        var web = new FakeSiteHandler();
        web.Status("https://gallery.test/api/g/denied", HttpStatusCode.Forbidden);
        web.Status("https://gallery.test/api/g/down", HttpStatusCode.BadGateway);
        using var host = Extensions.Host(web);
        var source = await Extensions.GallerySource(host);

        var denied = await Assert.ThrowsAsync<SourceException>(() => source.ResolveUrlAsync(new Uri("https://gallery.test/g/denied"), CancellationToken.None));
        Assert.Equal((SourceErrorKind.AuthFailed, "The token was refused."), (denied.Kind, denied.Message));
        var down = await Assert.ThrowsAsync<SourceException>(() => source.ResolveUrlAsync(new Uri("https://gallery.test/g/down"), CancellationToken.None));
        Assert.Equal(SourceErrorKind.Error, down.Kind);
        Assert.Null(await source.ResolveUrlAsync(new Uri("https://gallery.test/g/missing"), CancellationToken.None));
    }

    [Theory]
    [InlineData("Title", null, null, null, "Title")]
    [InlineData("Title", "2", "11", null, "Title — Vol. 2 Ch. 11")]
    [InlineData("Title", null, "11", "The end", "Title — Ch. 11: The end")]
    [InlineData("Title", null, null, "Oneshot", "Title — Oneshot")]
    public void AReleasesNameHasItsVolumeAndChapter(string title, string? volume, string? chapter, string? chapterTitle, string expected)
    {
        var metadata = new Shizuka.Sources.Abstractions.SourceMetadata
        {
            ExternalId = "1",
            Url = new Uri("https://gallery.test/g/1"),
            Title = title,
            Volume = volume,
            Chapter = chapter,
            Extra = chapterTitle is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["chapterTitle"] = chapterTitle },
        };

        Assert.Equal(expected, ExtensionSource.ReleaseTitle(metadata));
    }
}

public class ExtensionChallengeTests
{
    [Fact]
    public async Task APassedCheckPutsItsCookieInTheJarAndTheRequestGoesAgain()
    {
        var web = new FakeSiteHandler();
        web.Answer("https://gallery.test/api/g/g1", request =>
            FakeSiteHandler.Header(request, "Cookie")?.Contains("cf_clearance=ok", StringComparison.Ordinal) == true
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "title": "One" }""") }
                : FakeSiteHandler.CloudflarePage());
        var captcha = new FakeCaptchaService();
        captcha.Solves.Enqueue(FakeCaptchaService.Check("solved", cookies: [("cf_clearance", "ok")]));
        using var host = Extensions.Host(web, captcha: captcha);
        var source = await Extensions.GallerySource(host);

        var found = await source.ResolveUrlAsync(new Uri("https://gallery.test/g/g1"), CancellationToken.None);

        Assert.Equal("One", found!.Title);
        var solve = Assert.Single(captcha.Bodies("solve"));
        Assert.Equal("https://gallery.test/api/g/g1", (string?)solve["url"]);
        // The source's own profile at the service: its cookies never reach other sources or clients.
        Assert.Equal("gallery.test#convy.test-gallery", (string?)solve["profile"]);
        Assert.Equal(25, (int)solve["wait"]!);
        Assert.Equal("convy-key", captcha.Calls[0].Key);
        Assert.Equal("Browser/1", FakeSiteHandler.Header(web.Requests[^1], "User-Agent"));

        // Later calls carry the cookie from the start: no new check.
        await source.ResolveUrlAsync(new Uri("https://gallery.test/g/g1"), CancellationToken.None);
        Assert.Single(captcha.Bodies("solve"));
    }

    [Fact]
    public async Task ACheckThatWaitsForAPersonIsNamedByTheRestApi()
    {
        var id = Guid.NewGuid();
        var captcha = new FakeCaptchaService();
        captcha.Solves.Enqueue(FakeCaptchaService.Check("pending", id));
        using var h = new GalleryHarness(captcha);
        h.Web.Answer("https://gallery.test/api/g/g1", _ => FakeSiteHandler.CloudflarePage());

        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Api.ResolveAsync("test-gallery", "https://gallery.test/g/g1", CancellationToken.None));

        Assert.Equal((403, id), (ex.Status, ex.ChallengeId));
        Assert.Equal("https://gallery.test/api/g/g1", ex.ChallengeUrl?.AbsoluteUri);
        Assert.Contains("waits for someone to pass it", ex.Message);
        // The REST call waits only as long as its own time allows (5 s, less the margin for the answer).
        Assert.InRange((int)Assert.Single(captcha.Bodies("solve"))["wait"]!, 0, 1);
    }

    [Fact]
    public async Task WithoutAServiceACheckIsReportedAsSuch()
    {
        var web = new FakeSiteHandler();
        web.Answer("https://gallery.test/api/g/g1", _ => FakeSiteHandler.CloudflarePage());
        using var host = Extensions.Host(web);
        var source = await Extensions.GallerySource(host);

        var ex = await Assert.ThrowsAsync<SourceException>(() => source.ResolveUrlAsync(new Uri("https://gallery.test/g/g1"), CancellationToken.None));

        Assert.Equal(SourceErrorKind.Challenge, ex.Kind);
        Assert.Contains("No service of checks is set up", ex.Message);
        Assert.Equal("https://gallery.test/api/g/g1", ex.ChallengeUrl?.AbsoluteUri);
        Assert.Null(ex.ChallengeId);
    }

    [Fact]
    public async Task ASitesOwnCaptchaIsPassedAtTheServiceAndTheCallRunsAgain()
    {
        var web = new FakeSiteHandler();
        web.Answer("https://gallery.test/api/g/g1", request =>
        {
            if (FakeSiteHandler.Header(request, "Cookie")?.Contains("passed=1", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "title": "One" }""") };
            }

            var page = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            page.Headers.Add("X-Captcha", "https://gallery.test/captcha");
            return page;
        });
        var captcha = new FakeCaptchaService();
        captcha.Solves.Enqueue(FakeCaptchaService.Check("solved", cookies: [("passed", "1")]));
        using var host = Extensions.Host(web, captcha: captcha);
        var source = await Extensions.GallerySource(host);

        var found = await source.ResolveUrlAsync(new Uri("https://gallery.test/g/g1"), CancellationToken.None);

        Assert.Equal("One", found!.Title);
        var solve = Assert.Single(captcha.Bodies("solve"));
        Assert.Equal(("site", "https://gallery.test/captcha", true), ((string?)solve["kind"], (string?)solve["url"], (bool?)solve["doneWhen"]?["urlLeaves"]));
        Assert.Equal("gallery.test#convy.test-gallery", (string?)solve["profile"]);
    }
}
