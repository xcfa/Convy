using Convy.Services.Downloads;
using Convy.Services.Media;
using Convy.Services.Rules;
using Convy.Services.Security;
using Convy.Services.Ui;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class ConfigurationMaskingTests
{
    [Theory]
    [InlineData("QBitTorrent:Password", "hunter2")]
    [InlineData("Prowlarr:ApiKey", "abc123")]
    [InlineData("Slskd:ApiKey", "abc123")]
    [InlineData("Mcp:ApiKey", "abc123")]
    [InlineData("Ui:Oidc:ClientSecret", "s3cr3t")]
    [InlineData("Extensions:ui_indexes:0:password", "pw")]
    [InlineData("Extensions:ui_indexes:0:username", "share-token")]
    [InlineData("Extensions:indexes:1:username", "share-token")]
    [InlineData("Extensions:settings:e-hentai:values:cookies", "ipb_member_id=1")]
    [InlineData("Extensions:settings:my-site:values:hash", "a value only the manifest knows is secret")]
    [InlineData("Serilog:WriteTo:1:Args:apiKey", "seq-key")]
    [InlineData("Something:AccessToken", "t")]
    [InlineData("Webhooks:0:headers:0:value", "Bearer abc")]
    [InlineData("Webhooks:2:Headers:1:Value", "abc")]
    public void HidesValuesOfSecretKeys(string key, string value)
    {
        Assert.Equal(ConfigurationMasking.Hidden, ConfigurationMasking.Mask(key, value, out var hidden));
        Assert.True(hidden);
    }

    [Theory]
    [InlineData("Opds:catalogs:0:proxy", "socks5://tor:9050", "socks5://tor:9050", false)]
    [InlineData("Opds:catalogs:0:proxy", "http://user:pa55@proxy:3128", "http://user:••••••@proxy:3128", true)]
    [InlineData("Webhooks:0:Url", "https://hooks.example/x?token=abc&chat=5", "https://hooks.example/x?token=••••••&chat=5", true)]
    [InlineData("Webhooks:0:Url", "https://hooks.example/x?apikey=abc", "https://hooks.example/x?apikey=••••••", true)]
    [InlineData("ConnectionStrings:SQLite", "Data Source=/var/lib/convy/convy.db", "Data Source=/var/lib/convy/convy.db", false)]
    [InlineData("ConnectionStrings:Other", "Host=db;Username=u;Password=p4ss;Pooling=true", "Host=db;Username=u;Password=••••••;Pooling=true", true)]
    [InlineData("Ui:Oidc:ClientId", "convy", "convy", false)]
    [InlineData("Webhooks:0:headers:0:name", "Authorization", "Authorization", false)]
    [InlineData("Categories:music:path_hint", "Artist/Year - Album", "Artist/Year - Album", false)]
    public void HidesCredentialsInsideValues(string key, string value, string expected, bool wasHidden)
    {
        Assert.Equal(expected, ConfigurationMasking.Mask(key, value, out var hidden));
        Assert.Equal(wasHidden, hidden);
    }

    [Fact]
    public void LeavesEmptyValuesAlone()
    {
        Assert.Equal("", ConfigurationMasking.Mask("QBitTorrent:Password", "", out var hidden));
        Assert.False(hidden);
        Assert.Null(ConfigurationMasking.Mask("Mcp:ApiKey", null, out _));
    }
}

public sealed class AboutServiceTests
{
    private sealed class FakeInspector : IConfigurationInspector
    {
        public List<ConfigurationValue> Values { get; } = [];
        public List<string> Asked { get; } = [];

        public string EnvironmentName => "Production";
        public string ContentRoot => "/app";
        public IReadOnlyList<ConfigurationSourceInfo> Sources { get; } =
            [new("config/configuration.yml", "/app/config/configuration.yml", true), new("environment variables", null, null)];

        public string? this[string key] => Values.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        public IReadOnlyList<ConfigurationValue> Read(IReadOnlyCollection<string> sections)
        {
            Asked.AddRange(sections);
            return Values.Where(v => sections.Contains(v.Key.Split(':')[0], StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    private sealed class FakeDiagnostics : IRulesDiagnostics
    {
        public string Path => "/app/config/rules.yaml";
        public string? LastError => null;
        public DateTimeOffset? LastErrorAt => null;
    }

    /// <summary>The About page's service, with the sources already listed unless <paramref name="listed"/> is false.</summary>
    private static async Task<AboutService> Service(
        IConfigurationInspector inspector,
        UiOptions? ui = null,
        ISourceProvider? provider = null,
        Dictionary<string, CategoryOptions>? categories = null,
        IReadOnlyList<IDownloader>? downloaders = null,
        bool listed = true)
    {
        var registry = new SourceRegistry([provider ?? new StaticProvider()], Media.Health(), new FakeTime(), NullLogger<SourceRegistry>.Instance);
        if (listed)
        {
            await registry.RefreshAsync(CancellationToken.None);
        }

        return new AboutService(
            inspector,
            new FakeDiagnostics(),
            new DownloaderResolver(downloaders ?? [new FakeDownloader()]),
            registry,
            Media.Catalog(categories ?? Media.Categories()),
            new StaticOptions<SearchOptions>(new SearchOptions { SourceTimeoutSeconds = 20 }),
            ui ?? new UiOptions(),
            new StaticOptions<McpOptions>(new McpOptions { ApiKey = "k" }),
            new FakeTime());
    }

    [Fact]
    public async Task DoesNotWaitForTheSourcesToBeListed()
    {
        var provider = new StaticProvider(new FakeSource("prowlarr:1"));

        var about = (await Service(new FakeInspector(), provider: provider, listed: false)).Get();

        Assert.Null(about.SearchSources);
        Assert.Empty(about.SearchWarnings);
        Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData(null, null, null, null, null, null)]
    [InlineData("http://proxy:3128", null, null, "http://proxy:3128", null, null)]
    [InlineData("http://proxy:3128", "http://user:pa55@proxy:3128", "prowlarr,slskd", "http://proxy:3128", "http://user:••••••@proxy:3128", "prowlarr,slskd")]
    public async Task ShowsTheProxyFromTheEnvironment(
        string? http, string? https, string? noProxy, string? expectedHttp, string? expectedHttps, string? expectedNoProxy)
    {
        var inspector = new FakeInspector();
        foreach (var (name, value) in new[] { ("HTTP_PROXY", http), ("HTTPS_PROXY", https), ("NO_PROXY", noProxy) })
        {
            if (value is not null)
                inspector.Values.Add(new(name, value, "environment variables"));
        }

        var proxy = (await Service(inspector)).Get().Proxy;

        Assert.Equal((expectedHttp, expectedHttps, expectedNoProxy), (proxy.Http, proxy.Https, proxy.NoProxy));
        Assert.Empty(proxy.NoProxyIgnored);
    }

    [Fact]
    public async Task PointsOutNoProxyEntriesThatDotNetIgnores()
    {
        var inspector = new FakeInspector();
        inspector.Values.Add(new("HTTPS_PROXY", "http://mihomo:7890", "environment variables"));
        inspector.Values.Add(new("NO_PROXY", "localhost, prowlarr, .bakuretsu.org, *.bakuretsu.org, 172.16.0.0/12, *", "environment variables"));

        var proxy = (await Service(inspector)).Get().Proxy;

        Assert.Equal(["*.bakuretsu.org", "172.16.0.0/12", "*"], proxy.NoProxyIgnored);
    }

    [Fact]
    public async Task AllProxyServesBothSchemesUnlessOneHasItsOwn()
    {
        var inspector = new FakeInspector();
        inspector.Values.Add(new("ALL_PROXY", "socks5://tor:9050", "environment variables"));
        inspector.Values.Add(new("HTTP_PROXY", "http://proxy:3128", "environment variables"));

        var proxy = (await Service(inspector)).Get().Proxy;

        Assert.Equal(("http://proxy:3128", "socks5://tor:9050"), (proxy.Http, proxy.Https));
    }

    [Fact]
    public async Task ListsSearchSourcesWithTheirCategoriesAndWhatKeepsThemFromWorking()
    {
        var categories = Media.Categories("soulseek", "prowlarr:1", "soulsek", "prowlarr:2");
        categories["books"] = new CategoryOptions { Sources = ["opds:missing"] };
        var sources = new IContentSource[]
        {
            new FakeSource("prowlarr:1", "Rutracker"),
            new FakeSource("prowlarr:2", "Old tracker", status: SourceStatus.Disabled) { StatusMessage = "Disabled in Prowlarr" },
            new FakeSource("soulseek", "Soulseek", Protocol.Soulseek) { SourceTimeout = TimeSpan.FromMinutes(1) },
            new FakeSource("opds:flibusta", "Flibusta", Protocol.Opds, SourceStatus.Error) { StatusMessage = "Connection refused" },
        };
        var downloaders = new[] { new FakeDownloader(), new FakeDownloader(DownloadProviders.Slskd, Protocol.Soulseek) };

        var about = (await Service(new FakeInspector(), provider: new StaticProvider(sources), categories: categories, downloaders: downloaders))
            .Get();

        Assert.NotNull(about.SearchSources);
        Assert.Equal(["Flibusta", "Old tracker", "Rutracker", "Soulseek"], about.SearchSources.Select(s => s.Name));
        var source = about.SearchSources.ToDictionary(s => s.Id);

        // "other" lists nothing, so it searches every active source by name.
        Assert.Equal([("music", 2, true), ("other", 2, false)], Uses(source["prowlarr:1"]));
        Assert.Equal([("music", 1, true), ("other", 3, false)], Uses(source["soulseek"]));
        Assert.Equal([("other", 1, false)], Uses(source["opds:flibusta"]));
        Assert.Empty(source["prowlarr:2"].Categories);

        Assert.Equal(("torrent", "ok", 20d, "qbittorrent"), Facts(source["prowlarr:1"]));
        Assert.Equal(("soulseek", "ok", 60d, "slskd"), Facts(source["soulseek"]));
        Assert.Equal(("opds", "error", 20d, null), Facts(source["opds:flibusta"]));
        Assert.Equal("Connection refused", source["opds:flibusta"].Message);
        Assert.Equal(("disabled", "Disabled in Prowlarr"), (source["prowlarr:2"].Status, source["prowlarr:2"].Message));

        Assert.Equal(
        [
            "Category 'books' lists source 'opds:missing', which is not a known source.",
            "Category 'books' has no source to search.",
            "Category 'music' lists source 'soulsek', which is not a known source.",
            "Category 'music' lists source 'prowlarr:2', which is disabled.",
            "No downloader handles opds: results of these sources can be found but not downloaded.",
        ], about.SearchWarnings);

        static IEnumerable<(string, int, bool)> Uses(AboutSearchSource s) => s.Categories.Select(c => (c.Id, c.Position, c.Listed));
        static (string, string, double, string?) Facts(AboutSearchSource s) => (s.Protocol, s.Status, s.TimeoutSeconds, s.Downloader);
    }

    [Fact]
    public async Task ReportsAProviderThatCannotListItsSources()
    {
        var provider = new StaticProvider { Failure = new HttpRequestException("Connection refused (127.0.0.1:9696).") };
        var info = (await Service(new FakeInspector(), provider: provider)).Get();

        Assert.Empty(info.SearchSources!);
        Assert.Equal(
        [
            "Could not list the sources of static: Connection refused (127.0.0.1:9696).",
            "Category 'music' has no source to search.",
            "Category 'other' has no source to search.",
        ], info.SearchWarnings);
    }

    [Fact]
    public async Task ShowsVersionPathsSourcesAndMaskedSettingsInSectionOrder()
    {
        var inspector = new FakeInspector();
        inspector.Values.AddRange(
        [
            new("Ui:Oidc:ClientSecret", "s3cr3t", "Docker secrets"),
            new("QBitTorrent:Url", "http://qbittorrent:8080", "environment variables"),
            new("QBitTorrent:Password", "hunter2", "environment variables"),
            new("ConnectionStrings:SQLite", "Data Source=/var/lib/convy/convy.db", "appsettings.json"),
            new("Opds:downloads_path", "/data/downloads/opds", "config/configuration.yml"),
            new("ASPNETCORE_URLS", "http://+:8080", "environment variables"),
        ]);
        var ui = new UiOptions
        {
            Oidc = new UiOidcOptions { Authority = "https://auth.example.com", ClientId = "convy", ClientSecret = "s3cr3t" },
        };

        var about = (await Service(inspector, ui)).Get();

        Assert.Equal(AppInfo.Version, about.Version);
        Assert.Equal(("Production", "/app", "oidc", true), (about.Environment, about.ContentRoot, about.UiAuth, about.McpEnabled));
        Assert.Equal(["qbittorrent"], about.Downloaders);
        Assert.Equal(2, about.ConfigurationSources.Count);

        // Only Convy's sections, in the order of AboutService.Sections; secrets hidden.
        Assert.Equal(
            ["ConnectionStrings:SQLite", "QBitTorrent:Password", "QBitTorrent:Url", "Opds:downloads_path", "Ui:Oidc:ClientSecret"],
            about.Settings.Select(s => s.Key));
        var password = about.Settings.Single(s => s.Key == "QBitTorrent:Password");
        Assert.Equal((ConfigurationMasking.Hidden, true, "environment variables"), (password.Value, password.Hidden, password.Source));
        Assert.Equal("http://qbittorrent:8080", about.Settings.Single(s => s.Key == "QBitTorrent:Url").Value);
        Assert.DoesNotContain(about.Settings, s => s.Value?.Contains("s3cr3t") == true || s.Value?.Contains("hunter2") == true);

        Assert.Equal(["database", "rules", "sign-in keys", "book downloads (OPDS)"], about.Paths.Select(p => p.Name));
        Assert.Equal(Path.GetFullPath("/var/lib/convy/convy.db"), about.Paths[0].Path);
        Assert.Equal(Path.GetFullPath("/var/lib/convy/keys"), about.Paths[2].Path);
    }
}
