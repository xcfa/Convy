using System.Net;
using System.Text;
using Convy.Services.Plugins;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

/// <summary>Answers requests by absolute URL (query included); anything else is a 404. Records what was asked.</summary>
internal sealed class FakeSiteHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<string> Requested { get; } = [];

    public void Json(string url, string json) =>
        _routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public void Bytes(string url, byte[] bytes, string mediaType = "image/jpeg", long? announced = null) =>
        _routes[url] = _ =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            if (announced is { } length)
            {
                content.Headers.ContentLength = length;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };

    public void Status(string url, HttpStatusCode status) => _routes[url] = _ => new HttpResponseMessage(status);

    public void Answer(string url, Func<HttpRequestMessage, HttpResponseMessage> answer) => _routes[url] = answer;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.AbsoluteUri;
        lock (Requested) Requested.Add(key);
        var response = _routes.TryGetValue(key, out var route) ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}

internal static class Plugins
{
    public static string TestPluginPath => Path.Combine(AppContext.BaseDirectory, "plugins", "Convy.Sources.TestPlugin.dll");

    public static byte[] TestPlugin => File.ReadAllBytes(TestPluginPath);

    /// <summary>A host over a temporary plugins folder holding the test plugin, with <paramref name="web"/> as its transport.</summary>
    public static PluginHost Host(FakeSiteHandler web, PluginsOptions? options = null, FakeTime? time = null)
    {
        var folder = Directory.CreateTempSubdirectory("convy-plugins-").FullName;
        File.Copy(TestPluginPath, Path.Combine(folder, "Convy.Sources.TestPlugin.dll"));
        options ??= new PluginsOptions();
        options.Path = folder;
        options.Settings.TryAdd("test-gallery", new PluginEntryOptions { Values = { ["token"] = "secret" } });
        var host = new PluginHost(new StaticOptions<PluginsOptions>(options), time ?? new FakeTime(), NullLoggerFactory.Instance, _ => web);
        host.LoadAll();
        return host;
    }
}

public class PluginPackageTests
{
    [Fact]
    public void ReadsTheTestPluginAndItsApiVersion()
    {
        var package = PluginPackage.Read(Plugins.TestPlugin, trusted: false);

        Assert.Equal("Convy.Sources.TestPlugin", package.Package);
        Assert.Equal("1.0.0", package.VersionText);
        Assert.Equal(SourcesApi.Version.ToString(2), package.ApiVersion.ToString(2));
        Assert.True(package.IsCompatible);
        Assert.Equal(["Convy.Sources.TestPlugin"], package.Assemblies.Keys);
    }

    [Fact]
    public void RefusesWhatIsNotAPlugin()
    {
        var notDotNet = Assert.Throws<PluginPackageException>(() => PluginPackage.Read("hello"u8.ToArray(), trusted: true));
        Assert.Contains("not a .NET assembly", notDotNet.Message);

        // Convy.Data does not reference the API: no main assembly.
        var noMain = Assert.Throws<PluginPackageException>(() => PluginPackage.Read(File.ReadAllBytes(typeof(Convy.Data.Context.ConvyDbContext).Assembly.Location), trusted: true));
        Assert.Contains("not a Convy plugin", noMain.Message);
    }

    [Fact]
    public void TheAuditRefusesAnAssemblyThatOpensItsOwnConnections()
    {
        // Convy.Sources references the API and uses HttpClient (Prowlarr, slskd): what a plugin may not do.
        var bytes = File.ReadAllBytes(typeof(Convy.Sources.Prowlarr.ProwlarrSource).Assembly.Location);

        var refused = Assert.Throws<PluginPackageException>(() => PluginPackage.Read(bytes, trusted: false));
        Assert.Contains("System.Net.Http.HttpClient", refused.Message);
        Assert.Contains("plugins.trusted", refused.Message);

        Assert.Equal("Convy.Sources", PluginPackage.Read(bytes, trusted: true).Package);
    }

    [Fact]
    public void ReadsAZipOfTheMainAssembly()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("Convy.Sources.TestPlugin.dll").Open())
            {
                entry.Write(Plugins.TestPlugin);
            }

            using var readme = new StreamWriter(zip.CreateEntry("readme.txt").Open());
            readme.Write("hello");
        }

        Assert.Equal("Convy.Sources.TestPlugin", PluginPackage.Read(buffer.ToArray(), trusted: false).Package);
    }
}

public class PluginHostTests
{
    [Fact]
    public async Task LoadsThePluginsOfTheFolderAndListsTheirSources()
    {
        var web = new FakeSiteHandler();
        using var host = Plugins.Host(web);

        Assert.Empty(host.Problems);
        var plugin = Assert.Single(host.Plugins);
        Assert.Equal(("test-gallery", "Test gallery"), (plugin.Manifest.Id, plugin.Manifest.Name));
        var source = Assert.IsAssignableFrom<IGallerySource>(Assert.Single(await host.GetSourcesAsync(CancellationToken.None)));
        Assert.Equal(("test-gallery", Protocol.Gallery, SourceStatus.Ok), (source.Id, source.Protocol, source.Status));
        Assert.Equal(["gallery.test"], source.Manifest.Domains);
        Assert.Equal("category", Assert.Single(source.Manifest.Filters).Key);
        Assert.Same(plugin, host.PluginOf("test-gallery"));
    }

    [Fact]
    public async Task SettingsAreReadLiveAndADisabledPluginListsNothing()
    {
        var options = new PluginsOptions();
        options.Settings["test-gallery"] = new PluginEntryOptions { Values = { ["token"] = " " } };
        using var host = Plugins.Host(new FakeSiteHandler(), options);

        var source = Assert.Single(await host.GetSourcesAsync(CancellationToken.None));
        Assert.Equal((SourceStatus.Error, "Set the token."), (source.Status, source.StatusMessage));

        options.Settings["test-gallery"].Values["token"] = "abc";
        Assert.Equal(SourceStatus.Ok, source.Status);

        options.Settings["test-gallery"].Enabled = false;
        Assert.Empty(await host.GetSourcesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ThePluginsHttpStaysWithinTheManifestsDomains()
    {
        var web = new FakeSiteHandler();
        web.Json("https://gallery.test/api/g/g1", """{ "id": "g1", "title": "One", "pages": 2, "tags": ["a", "b"] }""");
        using var host = Plugins.Host(web);
        var source = (IGallerySource)Assert.Single(await host.GetSourcesAsync(CancellationToken.None));

        var found = await source.ResolveUrlAsync(new Uri("https://gallery.test/g/g1"), CancellationToken.None);
        Assert.Equal(("g1", "One", 2, "a, b"), (found!.ContentId, found.Title, found.FileCount, found.Properties["tags"]));
        Assert.Null(await source.ResolveUrlAsync(new Uri("https://elsewhere.test/g/g1"), CancellationToken.None));

        // The plugin asked for an image outside its domains: refused by the host, not sent.
        var refused = await Assert.ThrowsAsync<SourceException>(() => source.OpenImageAsync(new Uri("https://evil.test/x.jpg"), CancellationToken.None));
        Assert.Contains("not among the plugin's domains", refused.Message);
        Assert.DoesNotContain("https://evil.test/x.jpg", web.Requested);

        // A redirect that leaves the domains is refused too.
        web.Answer("https://img.gallery.test/leave.jpg", r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.other.test/x.jpg"),
            Content = new ByteArrayContent([1]),
        });
        var left = await Assert.ThrowsAsync<SourceException>(() => source.OpenImageAsync(new Uri("https://img.gallery.test/leave.jpg"), CancellationToken.None));
        Assert.Contains("redirected to cdn.other.test", left.Message);
    }

    [Fact]
    public void AFolderWithABrokenPackageReportsItAndLoadsTheRest()
    {
        var web = new FakeSiteHandler();
        var folder = Directory.CreateTempSubdirectory("convy-plugins-").FullName;
        File.Copy(Plugins.TestPluginPath, Path.Combine(folder, "good.dll"));
        File.WriteAllText(Path.Combine(folder, "broken.dll"), "not a dll");
        File.Copy(typeof(Convy.Sources.Prowlarr.ProwlarrSource).Assembly.Location, Path.Combine(folder, "goes-around.dll"));
        var options = new PluginsOptions { Path = folder };
        using var host = new PluginHost(new StaticOptions<PluginsOptions>(options), new FakeTime(), NullLoggerFactory.Instance, _ => web);

        host.LoadAll();

        Assert.Single(host.Plugins);
        Assert.Equal(["broken.dll", "goes-around.dll"], host.Problems.Select(p => p.File).Order());
        Assert.Contains("not a .NET assembly", host.Problems[0].Message);
        Assert.Contains("HttpClient", host.Problems[1].Message);
    }

    [Fact]
    public void NoFolderIsNoPlugins()
    {
        using var host = new PluginHost(
            new StaticOptions<PluginsOptions>(new PluginsOptions { Path = Path.Combine(Path.GetTempPath(), "convy-none-" + Guid.NewGuid()) }),
            new FakeTime(), NullLoggerFactory.Instance, _ => new FakeSiteHandler());

        host.LoadAll();

        Assert.Empty(host.Plugins);
        Assert.Empty(host.Problems);
    }

    [Fact]
    public void TheHtmlParserReadsMalformedMarkupAndResolvesLinks()
    {
        var html = new PluginHtml();

        var root = html.Parse("<div class=a><p>Hi <b>there</b><a href='/g/1/'>link<a href='https://x.test/i.jpg'>", new Uri("https://site.test/list"));

        Assert.Equal("html", root.TagName);
        Assert.Equal("Hi there", root.QuerySelector("div.a > p")!.Text.TrimEnd("link".ToCharArray()));
        Assert.Equal(new Uri("https://site.test/g/1/"), root.QuerySelector("a")!.GetUrl("href"));
        Assert.Equal(new Uri("https://x.test/i.jpg"), root.QuerySelectorAll("a")[1].GetUrl("href"));
        Assert.Null(root.QuerySelector("p")!.GetUrl("href"));
        var bad = Assert.Throws<SourceException>(() => root.QuerySelector("div["));
        Assert.Contains("selector", bad.Message);
    }
}
