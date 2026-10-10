using System.Text.Json;
using Convy.Services.Downloads;
using Convy.Services.Jobs;
using Convy.Services.Webhooks;

namespace Convy.Services.Tests;

public class JobStateTests
{
    private static JobRecord Release(int id, string title, JobStatus status, string? path = null, string? rule = "music",
        IReadOnlyList<string>? placed = null, string? error = null) => new()
    {
        Id = id,
        GroupId = 7,
        Provider = DownloadProviders.Slskd,
        ItemRef = title,
        Category = "music",
        Title = title,
        Status = status,
        Rule = rule,
        TargetPath = path,
        SizeBytes = 100,
        FileCount = 10,
        PlacedFiles = placed,
        Error = error,
    };

    [Theory]
    [InlineData("downloading,queued,completed", JobStatus.Downloading)]
    [InlineData("stalled,queued", JobStatus.Stalled)]
    [InlineData("queued,placing", JobStatus.Queued)]
    [InlineData("placing,completed", JobStatus.Placing)]
    [InlineData("failed,downloading", JobStatus.Downloading)]
    [InlineData("completed,failed,cancelled", JobStatus.Failed)]
    [InlineData("completed,cancelled", JobStatus.Cancelled)]
    [InlineData("completed,completed", JobStatus.Completed)]
    [InlineData("queued", JobStatus.Queued)]
    public void CombinesTheStatusesOfTheReleases(string statuses, JobStatus expected)
    {
        Assert.Equal(expected, JobState.Combine(statuses.Split(',').Select(JobStatusNames.Parse)));
    }

    [Theory]
    [InlineData(new[] { "/data/media/music/A/2003 - X", "/data/media/music/A/2006 - Y" }, "/data/media/music/A")]
    [InlineData(new[] { "/data/media/music/A/X", "/data/media/music/B/Y" }, "/data/media/music")]
    [InlineData(new[] { "/data/media/music/A/X", "/data/media/music/A/X/" }, "/data/media/music/A/X")]
    [InlineData(new[] { "/data/one", "/srv/two" }, "/")]
    public void CommonDirectoryIsTheDeepestSharedFolder(string[] paths, string expected)
    {
        Assert.Equal(expected, JobState.CommonDirectory(paths));
        Assert.Null(JobState.CommonDirectory([null, ""]));
    }

    [Fact]
    public void CombinesTitlePathRuleSizeFilesAndErrors()
    {
        var job = JobState.From([
            Release(8, "The Open Door", JobStatus.Failed, "/m/Evanescence/2006 - The Open Door", error: "peer offline"),
            Release(7, "Fallen", JobStatus.Completed, "/m/Evanescence/2003 - Fallen", placed: ["01.mp3", "CD2/01.mp3"]),
        ]);

        Assert.Equal("j_7", job.JobId);
        Assert.Equal(["Fallen", "The Open Door"], job.Releases.Select(r => r.Title));
        Assert.Equal("Fallen (+1 more)", job.Title);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("/m/Evanescence", job.TargetPath);
        Assert.Equal("music", job.Rule);
        Assert.Equal(200, job.SizeBytes);
        Assert.Equal(20, job.FileCount);
        Assert.Equal(["2003 - Fallen/01.mp3", "2003 - Fallen/CD2/01.mp3"], job.PlacedFiles);
        Assert.Equal("The Open Door: peer offline", job.Error);
    }

    [Fact]
    public void ASingleReleaseKeepsItsOwnValues()
    {
        var job = JobState.From([Release(7, "Fallen", JobStatus.Completed, "/m/Evanescence/2003 - Fallen", placed: ["01.mp3"], error: "x")]);

        Assert.Equal("Fallen", job.Title);
        Assert.Equal("/m/Evanescence/2003 - Fallen", job.TargetPath);
        Assert.Equal(["01.mp3"], job.PlacedFiles);
        Assert.Equal("x", job.Error);
    }

    [Fact]
    public void WebhookBodyListsTheReleasesAndEveryRule()
    {
        var job = JobState.From([
            Release(7, "Fallen", JobStatus.Completed, "/m/A/X", placed: ["01.mp3"]),
            Release(8, "Soundtrack", JobStatus.Completed, "/m/OST/Y", rule: "soundtracks", placed: ["01.mp3"]),
        ]);

        var webhookEvent = WebhookJobEvents.ToEvent(new JobStatusChange(job, JobStatus.Placing));
        var json = JsonSerializer.SerializeToElement(webhookEvent.Payload);

        Assert.Equal(["music", "soundtracks"], webhookEvent.RuleNames);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("rule").ValueKind);
        Assert.Equal("/m", json.GetProperty("path").GetString());
        Assert.Equal(["A/X/01.mp3", "OST/Y/01.mp3"], json.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        var releases = json.GetProperty("releases").EnumerateArray().ToList();
        Assert.Equal(["Fallen", "Soundtrack"], releases.Select(r => r.GetProperty("title").GetString()));
        Assert.Equal("/m/OST/Y", releases[1].GetProperty("path").GetString());
    }
}
