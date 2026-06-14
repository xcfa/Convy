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
        var mappings = ConvyMappings.Parse(
        [
            "# routing rules",
            "Category == Movies        => D:\\Movies",
            "Tags.Contains(anime)      => D:\\Anime",
            "Size > 0                  => D:\\Fallback",
        ]);

        Assert.Equal("D:\\Anime", mappings.Resolve(Anime()));
    }

    [Fact]
    public void ReturnsNullWhenNoRuleMatches()
    {
        var mappings = ConvyMappings.Parse(["Category == Movies => D:\\Movies"]);
        Assert.Null(mappings.Resolve(Anime()));
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        var mappings = ConvyMappings.Parse(
        [
            "",
            "# a comment",
            "; another comment",
            "   ",
            "Size > 0 => D:\\Out",
        ]);

        Assert.Single(mappings.Rules);
        Assert.Equal(1, mappings.Rules.Count);
    }

    [Fact]
    public void MissingSeparatorThrowsWithLineNumber()
    {
        var ex = Assert.Throws<FilterParseException>(() =>
            ConvyMappings.Parse(["Size > 0 D:\\Out"]));
        Assert.Contains("Line 1", ex.Message);
    }

    [Fact]
    public void EmptyOutputPathThrows()
    {
        Assert.Throws<FilterParseException>(() => ConvyMappings.Parse(["Size > 0 =>   "]));
    }

    [Fact]
    public void BadConditionReportsLineNumber()
    {
        var ex = Assert.Throws<FilterParseException>(() =>
            ConvyMappings.Parse(["Bogus == 1 => D:\\Out"]));
        Assert.Contains("Line 1", ex.Message);
    }
}
