using Convy.PathExpressions.Mappings;
using Convy.Services.Downloads;
using Convy.Services.Placement;

namespace Convy.Services.Tests;

public class SubpathValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingSubpathIsValidAndMeansNone(string? subpath)
    {
        Assert.True(SubpathValidator.TryValidate(subpath, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Show (2019)")]
    [InlineData("Evanescence/2003 - Fallen")]
    [InlineData("Фильмы/Брат (1997)")]
    public void AcceptsRelativePaths(string subpath)
    {
        Assert.True(SubpathValidator.TryValidate(subpath, out var normalized, out _));
        Assert.Equal(subpath, normalized);
    }

    [Theory]
    [InlineData("/abs/path", "relative")]
    [InlineData("\\abs", "relative")]
    [InlineData("a\\b", "'/'")]
    [InlineData("../escape", "'..'")]
    [InlineData("a/../../b", "'..'")]
    [InlineData("./a", "'.'")]
    [InlineData("a//b", "empty")]
    [InlineData("a/", "empty")]
    [InlineData("C:/x", "< > :")]
    [InlineData("what?", "< > :")]
    [InlineData("a*b", "< > :")]
    [InlineData("a|b", "< > :")]
    [InlineData("a\"b", "< > :")]
    [InlineData("tab\there", "control")]
    [InlineData(" lead", "whitespace")]
    [InlineData("trail /x", "whitespace")]
    public void RejectsInvalidSubpathsWithAReason(string subpath, string reason)
    {
        Assert.False(SubpathValidator.TryValidate(subpath, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void RejectsSegmentsLongerThan255Bytes()
    {
        // 128 Cyrillic letters are 256 UTF-8 bytes.
        Assert.False(SubpathValidator.TryValidate(new string('я', 128), out _, out var error));
        Assert.Contains("255 bytes", error);
        Assert.True(SubpathValidator.TryValidate(new string('я', 127), out _, out _));
    }

    [Theory]
    [InlineData("/data/media", "/data/media/Show", true)]
    [InlineData("/data/media", "/data/media", false)]
    [InlineData("/data/media", "/data/media-other/x", false)]
    [InlineData("/data/media", "/data/other", false)]
    public void IsInsideRequiresAStrictSubdirectory(string root, string candidate, bool expected) =>
        Assert.Equal(expected, SubpathValidator.IsInside(root, candidate));
}

public class PlacementPlannerTests
{
    private static readonly MappingRule Movies = Assert.Single(ConvyMappings.ParseYaml(
        """
        rules:
          - name: movies
            condition: "Category == Movies"
            path: /data/media/movies
        """).Rules);

    private static string N(string? path) => path is null ? "" : path.Replace('\\', '/');

    [Fact]
    public void RuleAndSubpathPlaceUnderRulePathReplacingRoot()
    {
        var target = PlacementPlanner.ResolveTarget(Movies, "Movie (2019)", "/data/downloads");

        Assert.Equal("movies", target.RuleName);
        Assert.Equal("/data/media/movies/Movie (2019)", N(target.Directory));
        Assert.Equal("/data/media/movies", target.BaseDirectory);
        Assert.True(target.ReplaceRoot);
    }

    [Fact]
    public void RuleWithoutSubpathKeepsOriginalStructure()
    {
        var target = PlacementPlanner.ResolveTarget(Movies, null, "/data/downloads");

        Assert.Equal("/data/media/movies", target.Directory);
        Assert.False(target.ReplaceRoot);
    }

    [Fact]
    public void SubpathWithoutRuleStaysInsideTheSavePath()
    {
        var target = PlacementPlanner.ResolveTarget(null, "Show (2019)", "/data/downloads");

        Assert.Null(target.RuleName);
        Assert.Equal("/data/downloads/Show (2019)", N(target.Directory));
        Assert.Equal("/data/downloads", target.BaseDirectory);
        Assert.True(target.ReplaceRoot);
    }

    [Fact]
    public void NeitherRuleNorSubpathLeavesFilesInPlace()
    {
        Assert.True(PlacementPlanner.ResolveTarget(null, null, "/data/downloads").LeaveInPlace);
    }

    [Fact]
    public void RootIsTheCommonFirstFolderOfAllFiles()
    {
        DownloadFile F(string p) => new(p, 1, 1, true);

        Assert.Equal("Movie.2019.WEB-DL", PlacementPlanner.FindRoot([F("Movie.2019.WEB-DL/movie.mkv"), F("Movie.2019.WEB-DL/Subs/en.srt")]));
        Assert.Null(PlacementPlanner.FindRoot([F("movie.mkv")]));                       // single file
        Assert.Null(PlacementPlanner.FindRoot([F("Season 01/e1.mkv"), F("Season 02/e1.mkv")])); // no common root
        Assert.Null(PlacementPlanner.FindRoot([F("Show/e1.mkv"), F("extra.nfo")]));
        Assert.Null(PlacementPlanner.FindRoot([]));
    }

    [Fact]
    public void SubpathReplacesTheRootFolder()
    {
        var links = PlacementPlanner.PlanLinks(
            ["Movie.2019.2160p.WEB-DL/movie.mkv", "Movie.2019.2160p.WEB-DL/Subs/en.srt"],
            "/data/media/movies/Movie (2019)",
            "Movie.2019.2160p.WEB-DL");

        Assert.Equal("/data/media/movies/Movie (2019)/movie.mkv", N(links[0].Destination));
        Assert.Equal("/data/media/movies/Movie (2019)/Subs/en.srt", N(links[1].Destination));
        Assert.Equal("Movie.2019.2160p.WEB-DL/movie.mkv", links[0].Source);
    }

    [Fact]
    public void SingleFileGoesStraightIntoTheSubpath()
    {
        var link = Assert.Single(PlacementPlanner.PlanLinks(["movie.mkv"], "/data/media/movies/Movie (2019)", stripRoot: null));
        Assert.Equal("/data/media/movies/Movie (2019)/movie.mkv", N(link.Destination));
    }

    [Theory]
    [InlineData("Show/Season 02/e1.mkv", true)]   // original layout: root folder included
    [InlineData("Season 02/e1.mkv", true)]        // no-subfolder layout
    [InlineData("Show/Season 01/e1.mkv", false)]
    public void SelectionMatchesWithOrWithoutRootFolder(string path, bool expected) =>
        Assert.Equal(expected, PlacementPlanner.IsInSelection(path, new HashSet<string> { "Season 02/e1.mkv" }));

    [Fact]
    public void NoSelectionMeansEverything() =>
        Assert.True(PlacementPlanner.IsInSelection("anything", null));
}
