using System.Security.Cryptography;
using Convy.Services.Extensions;
using Convy.Services.Media;
using Convy.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shizuka.Sources.Hosting;

namespace Convy.Services.Tests;

/// <summary>The settings database as a dictionary: what the Sources page saved, applied to the options as the configuration would.</summary>
internal sealed class FakeUserSettings(ExtensionsOptions options) : IUserSettingsService
{
    public Dictionary<string, string> Saved { get; } = new(StringComparer.Ordinal);

    public Task UpdateAsync(UserSettingsUpdate update, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task WriteAsync(IReadOnlyDictionary<string, string?> entries, string? replace, CancellationToken cancellationToken)
    {
        if (replace is not null)
        {
            foreach (var key in Saved.Keys.Where(k => k.StartsWith(replace, StringComparison.Ordinal) && !entries.ContainsKey(k)).ToList())
            {
                Saved.Remove(key);
            }
        }

        foreach (var (key, value) in entries)
        {
            if (value is null)
            {
                Saved.Remove(key);
            }
            else
            {
                Saved[key] = value;
            }
        }

        Apply();
        return Task.CompletedTask;
    }

    /// <summary>What the configuration binder would make of the saved settings (those the tests use).</summary>
    private void Apply()
    {
        options.UiIndexes = Saved.Where(s => s.Key.StartsWith("Extensions:ui_indexes:", StringComparison.Ordinal) && s.Key.EndsWith(":url", StringComparison.Ordinal))
            .Select(s => new ExtensionIndexOptions { Url = s.Value })
            .ToList();
        foreach (var (key, value) in Saved.Where(s => s.Key.StartsWith("Extensions:settings:", StringComparison.Ordinal)))
        {
            var parts = key.Split(':');
            if (!options.Settings.TryGetValue(parts[2], out var entry))
            {
                options.Settings[parts[2]] = entry = new ExtensionEntryOptions();
            }

            switch (parts[3])
            {
                case "enabled":
                    entry.Enabled = value == "true";
                    break;
                case "proxy":
                    entry.Proxy = value;
                    break;
                case "values":
                    entry.Values[parts[4]] = value;
                    break;
            }
        }
    }
}

public sealed class ExtensionAdminTests : IDisposable
{
    private const string IndexUrl = "https://index.test/index.json";

    private readonly FakeSiteHandler _web = new();
    private readonly ExtensionsOptions _options = new();
    private readonly ExtensionHost _host;
    private readonly SourceRegistry _registry;
    private readonly FakeUserSettings _settings;
    private readonly ExtensionAdminService _admin;
    private readonly byte[] _package = File.ReadAllBytes(Extensions.ContentExtensionPath);

    public ExtensionAdminTests()
    {
        _options.Indexes = [new ExtensionIndexOptions { Url = IndexUrl }];
        // An empty folder: the tests install into it.
        _host = Extensions.ContentHost(_web, _options);
        foreach (var file in Directory.GetFiles(_options.Path))
        {
            File.Delete(file);
        }

        _host.Unload("Convy.Tests.ContentExtension");
        _registry = new SourceRegistry([_host], Media.Health(), new FakeTime(), NullLogger<SourceRegistry>.Instance);
        _settings = new FakeUserSettings(_options);
        _admin = new ExtensionAdminService(
            _host, new ExtensionIndexClient(new SourceTransport(_web), new ExtensionIndexLimits(), new FakeTime()), _settings, _registry,
            new StaticOptions<ExtensionsOptions>(_options), NullLogger<ExtensionAdminService>.Instance);
        _web.Json("http://indexers.test/api/indexers", """[ { "id": 1, "name": "Tracker one" } ]""");
    }

    public void Dispose()
    {
        _registry.Dispose();
        _host.Dispose();
        Directory.Delete(_options.Path, recursive: true);
    }

    /// <summary>An index with the test package for Convy at <paramref name="version"/>, a site of Shizuka's and a package for another app.</summary>
    private void Index(string version = "1.0.0", string requires = "2.0")
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(_package));
        _web.Json(IndexUrl, $$"""
            { "extensions": [
              { "package": "Convy.Tests.ContentExtension", "name": "Test content", "version": "{{version}}", "api": "1.6",
                "requires": { "Convy.Sources.Abstractions": "{{requires}}" }, "apps": ["convy"],
                "file": "packages/content.dll", "sha256": "{{hash}}", "sources": [ { "id": "test-books", "name": "Test books" } ] },
              { "package": "Shizuka.Sources.Catalog", "name": "Catalog", "version": "1.0.0", "api": "1.6",
                "file": "packages/catalog.zip", "sha256": "{{hash}}" },
              { "package": "Other.Sources.Thing", "name": "Thing", "version": "1.0.0", "api": "1.6", "apps": ["convy"],
                "requires": { "Other.Sources.Abstractions": "1.0" }, "file": "packages/thing.zip", "sha256": "{{hash}}" }
            ] }
            """);
        _web.Bytes("https://index.test/packages/content.dll", _package, "application/octet-stream");
    }

    [Fact]
    public async Task ListsWhatTheIndexesHaveForConvyAndWhyItCannotRunSome()
    {
        Index();

        var available = await _admin.AvailableAsync(fresh: true, CancellationToken.None);

        Assert.Empty(available.Errors);
        Assert.Equal(["Test content", "Thing"], available.Extensions.Select(e => e.Name));
        Assert.Null(available.Extensions[0].Incompatible);
        Assert.Equal((IndexUrl, "1.0.0", false, (string?)null), (available.Extensions[0].Index, available.Extensions[0].Version, available.Extensions[0].Update, available.Extensions[0].Installed));
        Assert.Contains("Other.Sources.Abstractions, which Convy does not provide", available.Extensions[1].Incompatible);
    }

    [Fact]
    public async Task InstallsAPackageFromAnIndexLoadsItAndListsItsSources()
    {
        Index();

        var installed = await _admin.InstallAsync(IndexUrl, "convy.tests.contentextension", CancellationToken.None);

        Assert.Equal(("Convy.Tests.ContentExtension", "1.0.0", "Convy.Tests.ContentExtension.dll", "2.0"), (installed.Package, installed.Version, installed.File, installed.ConvyApi));
        Assert.True(File.Exists(Path.Combine(_options.Path, "Convy.Tests.ContentExtension.dll")));
        Assert.Equal(["test-books", "test-peers", "test-indexers"], installed.Units.Select(u => u.Id));
        Assert.Equal(["files", "soulseek", "provider"], installed.Units.Select(u => u.Kind));
        // The token the test host set; its value is never sent back.
        var books = installed.Units[0];
        Assert.Null(books.Settings[0].Value);
        Assert.Equal(("token", "secret", true), (books.Settings[0].Key, books.Settings[0].Kind, books.Settings[0].Set));
        Assert.Contains((await _registry.GetSourcesAsync(CancellationToken.None)), s => s.Id == "test-books");

        var available = await _admin.AvailableAsync(fresh: false, CancellationToken.None);
        Assert.Equal(("1.0.0", false), (available.Extensions[0].Installed, available.Extensions[0].Update));
    }

    [Fact]
    public async Task RefusesAPackageThatDoesNotMatchTheIndexOrThatConvyCannotRun()
    {
        Index(requires: "3.0");
        var newer = await Assert.ThrowsAsync<ConvyRequestException>(() => _admin.InstallAsync(IndexUrl, "Convy.Tests.ContentExtension", CancellationToken.None));
        Assert.Contains("Convy.Sources.Abstractions", newer.Message);

        Index();
        _web.Bytes("https://index.test/packages/content.dll", [1, 2, 3], "application/octet-stream");
        var tampered = await Assert.ThrowsAsync<ConvyRequestException>(() => _admin.InstallAsync(IndexUrl, "Convy.Tests.ContentExtension", CancellationToken.None));
        Assert.Contains("does not match the index", tampered.Message);
        Assert.Empty(Directory.GetFiles(_options.Path));

        await Assert.ThrowsAsync<ConvyRequestException>(() => _admin.InstallAsync("https://elsewhere.test/index.json", "Convy.Tests.ContentExtension", CancellationToken.None));
    }

    [Fact]
    public async Task RemovesAPackageWithItsSources()
    {
        await _admin.UploadAsync(_package, CancellationToken.None);
        Assert.Single(_host.Extensions);

        await _admin.RemoveAsync("Convy.Tests.ContentExtension", CancellationToken.None);

        Assert.Empty(_host.Extensions);
        Assert.Empty(Directory.GetFiles(_options.Path));
        Assert.DoesNotContain(await _registry.GetSourcesAsync(CancellationToken.None), s => s.Id == "test-books");
    }

    [Fact]
    public async Task SavesASourcesSettingsCheckedByTheirKindAndKeepsASecretLeftOut()
    {
        await _admin.UploadAsync(_package, CancellationToken.None);

        await _admin.SaveSettingsAsync("test-indexers", new SourceSettingsInput(null, "socks5://tor:9050", 90,
            new Dictionary<string, string?> { ["url"] = "http://indexers.test/", ["key"] = "k1" }), CancellationToken.None);
        await _admin.SaveSettingsAsync("test-indexers", new SourceSettingsInput(false, null, null,
            new Dictionary<string, string?> { ["key"] = null }), CancellationToken.None);

        Assert.Equal("http://indexers.test/", _settings.Saved["Extensions:settings:test-indexers:values:url"]);
        Assert.Equal("k1", _settings.Saved["Extensions:settings:test-indexers:values:key"]);
        Assert.Equal(("socks5://tor:9050", "90", "false"), (_settings.Saved["Extensions:settings:test-indexers:proxy"],
            _settings.Saved["Extensions:settings:test-indexers:timeout_sec"], _settings.Saved["Extensions:settings:test-indexers:enabled"]));
        var provider = _admin.Get().Extensions.Single().Units.Single(u => u.Id == "test-indexers");
        Assert.Equal((false, "disabled"), (provider.Enabled, provider.Status));
        Assert.Equal((null, true), (provider.Settings.Single(s => s.Key == "key").Value, provider.Settings.Single(s => s.Key == "key").Set));

        var bad = await Assert.ThrowsAsync<ConvyRequestException>(() => _admin.SaveSettingsAsync("test-indexers",
            new SourceSettingsInput(null, null, null, new Dictionary<string, string?> { ["url"] = "ftp://x" }), CancellationToken.None));
        Assert.StartsWith("Address:", bad.Message);
        await Assert.ThrowsAsync<ConvyRequestException>(() => _admin.SaveSettingsAsync("test-indexers",
            new SourceSettingsInput(null, null, null, new Dictionary<string, string?> { ["nope"] = "1" }), CancellationToken.None));
        await Assert.ThrowsAsync<ConvyRequestException>(() => _admin.SaveSettingsAsync("nothing", new SourceSettingsInput(true, null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task AProvidersSourceIsOnlySwitchedOnOrOff()
    {
        await _admin.UploadAsync(_package, CancellationToken.None);
        await _registry.RefreshNowAsync(CancellationToken.None);

        await _admin.SaveSettingsAsync("test-indexers-1", new SourceSettingsInput(false, null, null, null), CancellationToken.None);

        Assert.Equal("false", _settings.Saved["Extensions:settings:test-indexers-1:enabled"]);
        var listed = Assert.Single(_admin.Get().Extensions.Single().Units.Single(u => u.Id == "test-indexers").Sources!);
        Assert.Equal(("test-indexers-1", "Tracker one", "torrent", false), (listed.Id, listed.Name, listed.Kind, listed.Enabled));
        await Assert.ThrowsAsync<ConvyRequestException>(() => _admin.SaveSettingsAsync("test-indexers-1",
            new SourceSettingsInput(null, "socks5://tor:9050", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task AddsAndRemovesTheIndexesOfThePageAndLeavesThoseOfTheFile()
    {
        await _admin.AddIndexAsync(new IndexInput("https://more.test/index.json", null, null, "t0ken"), CancellationToken.None);

        Assert.Equal([(IndexUrl, "file"), ("https://more.test/index.json", "ui")], _admin.Get().Indexes.Select(i => (i.Url, i.Source)));
        Assert.Equal("t0ken", _settings.Saved["Extensions:ui_indexes:0:token"]);
        await Assert.ThrowsAsync<ConvyRequestException>(() => _admin.AddIndexAsync(new IndexInput(IndexUrl, null, null, null), CancellationToken.None));
        await Assert.ThrowsAsync<ConvyRequestException>(() => _admin.RemoveIndexAsync(IndexUrl, CancellationToken.None));

        await _admin.RemoveIndexAsync("https://more.test/index.json", CancellationToken.None);

        Assert.Equal([IndexUrl], _admin.Get().Indexes.Select(i => i.Url));
        Assert.Empty(_settings.Saved);
    }
}
