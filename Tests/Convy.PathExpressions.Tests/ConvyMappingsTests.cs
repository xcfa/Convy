using Banned.Qbittorrent.Models.Torrent;
using Convy.PathExpressions.Mappings;
using Convy.PathExpressions.Parsing;
using Xunit;

namespace Convy.PathExpressions.Tests;

public class ConvyMappingsTests
{
    private static TorrentInfo Anime() => new()
    {
        Size = 500,
        Category = "Series",
        TagList = ["anime"],
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
