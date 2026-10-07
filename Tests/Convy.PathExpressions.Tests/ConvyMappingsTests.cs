using Convy.PathExpressions.Mappings;
using Convy.PathExpressions.Parsing;
using Xunit;

namespace Convy.PathExpressions.Tests;

public class ConvyMappingsTests
{
    private static Dictionary<string, object?> Anime() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Size"] = 500.0,
        ["Category"] = "Series",
        ["Tags"] = new[] { "anime" },
    };

    [Fact]
    public void FirstMatchingRuleWins()
    {
        var mappings = ConvyMappings.ParseYaml(
            """
            rules:
              - condition: "Category == Movies"
                path: /data/media/movies
              - condition: "Tags.Contains(anime)"
                path: /data/media/anime
              - condition: "Size > 0"
                path: /data/media/fallback
            """);

        Assert.Equal("/data/media/anime", mappings.Resolve(Anime()));
    }

    [Fact]
    public void ReturnsNullWhenNoRuleMatches()
    {
        var mappings = ConvyMappings.ParseYaml(
            """
            rules:
              - condition: "Category == Movies"
                path: /data/media/movies
            """);

        Assert.Null(mappings.Resolve(Anime()));
    }

    private static Dictionary<string, object?> SoulseekAlbum() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Provider"] = "slskd",
        ["Name"] = "Fallen",
        ["Size"] = 432000000.0,
        ["Category"] = "Music",
    };

    [Fact]
    public void RuleReferencingAMissingPropertyIsSkippedAsAWhole()
    {
        var mappings = ConvyMappings.ParseYaml(
            """
            rules:
              - name: keep-unless-skip
                condition: "!Tags.Contains(skip)"
                path: /data/media/kept
              - name: seeded
                condition: "Ratio >= 2 || Category == Music"
                path: /data/media/seeded
              - name: soulseek
                condition: "Provider == slskd"
                path: /data/media/soulseek
            """);

        // A Soulseek item has neither Tags nor Ratio: both rules are skipped, even though
        // the first would be "true" and the second has a matching Category alternative.
        Assert.Equal("soulseek", mappings.ResolveRule(SoulseekAlbum())?.Name);
    }

    [Fact]
    public void PresentButNullPropertyStillTakesPartInTheEvaluation()
    {
        var mappings = ConvyMappings.ParseYaml(
            """
            rules:
              - condition: "!Tags.Contains(skip)"
                path: /data/media/kept
            """);

        var torrent = new Dictionary<string, object?> { ["Provider"] = "qbittorrent", ["Tags"] = null };

        Assert.Equal("/data/media/kept", mappings.Resolve(torrent));
    }

    [Fact]
    public void ReferencedPropertiesAreCollectedWithCanonicalNames()
    {
        var rule = Assert.Single(ConvyMappings.ParseYaml(
            """
            rules:
              - condition: "(size > 1 || provider == slskd) && !tags.Contains(x)"
                path: /p
            """).Rules);

        Assert.Equal(new[] { "Provider", "Size", "Tags" }, rule.ReferencedProperties.Order());
    }

    [Fact]
    public void EmptyDocumentYieldsNoRules()
    {
        Assert.Empty(ConvyMappings.ParseYaml("").Rules);
        Assert.Empty(ConvyMappings.ParseYaml("rules: []").Rules);
    }

    [Fact]
    public void MissingConditionThrows()
    {
        var ex = Assert.Throws<FilterParseException>(() => ConvyMappings.ParseYaml(
            """
            rules:
              - path: /data/media/movies
            """));
        Assert.Contains("Rule #1", ex.Message);
    }

    [Fact]
    public void MissingPathThrows()
    {
        var ex = Assert.Throws<FilterParseException>(() => ConvyMappings.ParseYaml(
            """
            rules:
              - condition: "Size > 0"
            """));
        Assert.Contains("Rule #1", ex.Message);
    }

    [Fact]
    public void BadConditionReportsRuleIndex()
    {
        var ex = Assert.Throws<FilterParseException>(() => ConvyMappings.ParseYaml(
            """
            rules:
              - condition: "Category == Movies"
                path: /data/media/movies
              - condition: "Bogus == 1"
                path: /data/media/bogus
            """));
        Assert.Contains("Rule #2", ex.Message);
    }

    [Fact]
    public void InvalidYamlThrows()
    {
        Assert.Throws<FilterParseException>(() => ConvyMappings.ParseYaml("rules: [ unclosed"));
    }
}
