using Banned.Qbittorrent.Models.Enums;
using Banned.Qbittorrent.Models.Torrent;
using Convy.PathExpressions.Parsing;
using Xunit;

namespace Convy.PathExpressions.Tests;

public class TorrentFilterTests
{
    private static TorrentInfo Sample() => new()
    {
        Size = 500,
        Uploaded = 0,
        Category = "Test",
        TagList = ["Test", "anime"],
        Ratio = 1.8f,
        Name = "My Favourite Show",
        SeedingTime = TimeSpan.FromHours(30),
        AutoTmmEnabled = true,
        State = EnumTorrentState.StalledUpload,
    };

    private static bool Eval(string filter) => TorrentFilter.Parse(filter).Evaluate(Sample());

    [Fact]
    public void ExampleFromSpecMatches()
    {
        Assert.True(Eval("Size > 100 && Uploaded == 0 && Tags.Contains(Test) && Category == Test"));
    }

    [Theory]
    [InlineData("Size > 100", true)]
    [InlineData("Size > 1000", false)]
    [InlineData("Size >= 500", true)]
    [InlineData("Size < 500", false)]
    [InlineData("Size <= 500", true)]
    [InlineData("Size == 500", true)]
    [InlineData("Size != 500", false)]
    public void NumericComparisons(string filter, bool expected) => Assert.Equal(expected, Eval(filter));

    [Theory]
    [InlineData("Ratio >= 1.5", true)]
    [InlineData("Ratio > 2.0", false)]
    public void FloatComparisons(string filter, bool expected) => Assert.Equal(expected, Eval(filter));

    [Theory]
    [InlineData("Category == Test", true)]
    [InlineData("Category == test", true)]   // case-insensitive
    [InlineData("Category == Movies", false)]
    [InlineData("Category != Movies", true)]
    [InlineData("Name == \"My Favourite Show\"", true)]
    public void StringComparisons(string filter, bool expected) => Assert.Equal(expected, Eval(filter));

    [Theory]
    [InlineData("AutoTmmEnabled == true", true)]
    [InlineData("AutoTmmEnabled == false", false)]
    [InlineData("AutoTmmEnabled != false", true)]
    public void BoolComparisons(string filter, bool expected) => Assert.Equal(expected, Eval(filter));

    [Theory]
    [InlineData("Tags.Contains(Test)", true)]
    [InlineData("Tags.Contains(ANIME)", true)]   // case-insensitive membership
    [InlineData("Tags.Contains(missing)", false)]
    [InlineData("!Tags.Contains(skip)", true)]
    public void Contains(string filter, bool expected) => Assert.Equal(expected, Eval(filter));

    [Theory]
    [InlineData("Category == Movies || Category == Test", true)]
    [InlineData("Size > 1000 && Category == Test", false)]
    [InlineData("(Size > 1000 || Ratio >= 1.5) && Category == Test", true)]
    [InlineData("!(Category == Movies)", true)]
    public void LogicalOperatorsAndPrecedence(string filter, bool expected) =>
        Assert.Equal(expected, Eval(filter));

    [Theory]
    [InlineData("SeedingTime > 86400", true)]      // 30h > 24h, durations are seconds
    [InlineData("SeedingTime < 3600", false)]
    public void DurationIsProjectedToSeconds(string filter, bool expected) =>
        Assert.Equal(expected, Eval(filter));

    [Theory]
    [InlineData("State == StalledUpload", true)]
    [InlineData("State == Downloading", false)]
    public void EnumStateMatchesByName(string filter, bool expected) =>
        Assert.Equal(expected, Eval(filter));

    [Fact]
    public void NullPropertyDoesNotMatch()
    {
        // Category left null -> any comparison against it is false, not an exception.
        var info = new TorrentInfo { Size = 10 };
        Assert.False(TorrentFilter.Parse("Category == Test").Evaluate(info));
        Assert.False(TorrentFilter.Parse("Tags.Contains(x)").Evaluate(info));
    }

    [Theory]
    [InlineData("Bogus == 1")]                 // unknown property
    [InlineData("Category > 5")]               // relational op on a string
    [InlineData("AutoTmmEnabled > 1")]         // relational op on a bool
    [InlineData("Size > abc")]                 // non-numeric literal
    [InlineData("Tags == x")]                  // comparing a list
    [InlineData("Category.Contains(x)")]       // .Contains on a non-list
    [InlineData("Size >")]                     // syntax error
    [InlineData("")]                           // empty
    public void InvalidFiltersThrow(string filter) =>
        Assert.Throws<FilterParseException>(() => TorrentFilter.Parse(filter));

    [Fact]
    public void TryParseReportsErrorWithoutThrowing()
    {
        bool ok = TorrentFilter.TryParse("Bogus == 1", out var expr, out var error);
        Assert.False(ok);
        Assert.Null(expr);
        Assert.NotNull(error);
    }
}
