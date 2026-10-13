using Convy.Services.Jobs;
using Convy.Services.Webhooks;

namespace Convy.Services.Tests;

public class JobInstructionTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(" ", "", null)]
    [InlineData("Tag with beets.", null, "Tag with beets.")]
    [InlineData(null, " Convert to EPUB. ", "Convert to EPUB.")]
    [InlineData("Tag with beets.\n", "Keep the year.", "Tag with beets.\n\nKeep the year.")]
    public void CombinesTheRulesInstructionFirst(string? rule, string? request, string? expected) =>
        Assert.Equal(expected, JobInstructions.Combine(rule, request));

    private static JobRecord Release(int id, string title, string? ruleInstruction, string? destination) => new()
    {
        Id = id,
        GroupId = 1,
        Provider = "qbittorrent",
        ItemRef = "h" + id,
        Category = "music",
        Title = title,
        Status = JobStatus.Completed,
        TargetPath = "/data/processing/j_1",
        PlacedFiles = [title + "/01.flac"],
        Instruction = "Keep the year.",
        RuleInstruction = ruleInstruction,
        Destination = destination,
    };

    [Fact]
    public void JobStatusCarriesTheInstructionAndTheDestination()
    {
        var job = new JobState { Id = 1, Releases = [Release(1, "Album", "Tag with beets.", "/data/music/Artist")], Status = JobStatus.Completed };

        var payload = WebhookJobEvents.ToEvent(new JobStatusChange(job, JobStatus.Placing)).Payload;

        Assert.Equal("Tag with beets.\n\nKeep the year.", payload["instruction"]);
        Assert.Equal("/data/processing/j_1", payload["path"]);
        Assert.Equal("/data/music/Artist", payload["destination"]);
        Assert.Equal(new[] { "Album/01.flac" }, (IEnumerable<string>)payload["files"]!);
    }

    [Fact]
    public void ReleasesWithDifferentInstructionsKeepTheirOwn()
    {
        var job = new JobState
        {
            Id = 1,
            Releases = [Release(1, "Album", "Tag with beets.", "/data/music/A"), Release(2, "Single", null, "/data/music/B")],
            Status = JobStatus.Completed,
        };

        var payload = WebhookJobEvents.ToEvent(new JobStatusChange(job, JobStatus.Placing)).Payload;

        Assert.Null(payload["instruction"]);
        Assert.Equal("/data/music", payload["destination"]);
        var releases = (IEnumerable<Dictionary<string, object?>>)payload["releases"]!;
        Assert.Equal(new[] { "Tag with beets.\n\nKeep the year.", "Keep the year." }, releases.Select(r => r["instruction"]));
    }
}
