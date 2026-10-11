using Convy.Services.Downloads;
using Convy.Services.Rules;
using Convy.Services.Security;
using Convy.Services.Ui;

namespace Convy.Services.Tests;

public class ConfigurationMaskingTests
{
    [Theory]
    [InlineData("QBitTorrent:Password", "hunter2")]
    [InlineData("Prowlarr:ApiKey", "abc123")]
    [InlineData("Slskd:ApiKey", "abc123")]
    [InlineData("Mcp:ApiKey", "abc123")]
    [InlineData("Ui:Oidc:ClientSecret", "s3cr3t")]
    [InlineData("Opds:catalogs:0:password", "pw")]
    [InlineData("Serilog:WriteTo:1:Args:apiKey", "seq-key")]
    [InlineData("Something:AccessToken", "t")]
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

    [Fact]
    public void ShowsVersionPathsSourcesAndMaskedSettingsInSectionOrder()
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

        var about = new AboutService(
            inspector, new FakeDiagnostics(), new DownloaderResolver([new FakeDownloader()]), ui,
            new StaticOptions<McpOptions>(new McpOptions { ApiKey = "k" }), new FakeTime()).Get();

        Assert.Equal(AppInfo.Version, about.Version);
        Assert.Equal(("Production", "/app", "oidc", true), (about.Environment, about.ContentRoot, about.UiAuth, about.McpEnabled));
        Assert.Equal(["qbittorrent"], about.Downloaders);
        Assert.Equal(2, about.Sources.Count);

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
