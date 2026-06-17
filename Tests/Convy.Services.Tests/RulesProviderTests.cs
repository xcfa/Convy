using Convy.Services.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Convy.Services.Tests;

public class RulesProviderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"convy-rules-{Guid.NewGuid():N}.yaml");
    private static readonly DateTime T0 = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private void Write(string yaml, DateTime mtimeUtc)
    {
        File.WriteAllText(_path, yaml);
        File.SetLastWriteTimeUtc(_path, mtimeUtc); // make reload detection deterministic
    }

    private RulesProvider Create() => new(_path, NullLogger<RulesProvider>.Instance);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void MissingFileYieldsEmptyVersionZero()
    {
        var snapshot = Create().GetCurrent();

        Assert.Equal(0, snapshot.Version);
        Assert.Empty(snapshot.Mappings.Rules);
    }

    [Fact]
    public void LoadsRulesAndBumpsVersionOnChange()
    {
        Write("rules:\n  - condition: \"Size > 0\"\n    path: /data/a\n", T0);
        var provider = Create();

        var first = provider.GetCurrent();
        Assert.Equal(1, first.Version);
        Assert.Single(first.Mappings.Rules);

        // Unchanged file -> same version.
        Assert.Equal(1, provider.GetCurrent().Version);

        // Edited file (new mtime) -> reloaded, version bumped.
        Write("rules:\n  - condition: \"Size > 0\"\n    path: /data/a\n  - condition: \"Category == X\"\n    path: /data/b\n", T0.AddSeconds(5));
        var second = provider.GetCurrent();
        Assert.Equal(2, second.Version);
        Assert.Equal(2, second.Mappings.Rules.Count);
    }

    [Fact]
    public void KeepsPreviousRulesOnParseError()
    {
        Write("rules:\n  - condition: \"Size > 0\"\n    path: /data/a\n", T0);
        var provider = Create();
        Assert.Equal(1, provider.GetCurrent().Version);

        // A broken edit must not bump the version or drop the good rules.
        Write("rules:\n  - condition: \"Bogus == 1\"\n    path: /data/a\n", T0.AddSeconds(5));
        var snapshot = provider.GetCurrent();

        Assert.Equal(1, snapshot.Version);
        Assert.Single(snapshot.Mappings.Rules);
    }
}
