using System.Net;
using System.Text;
using System.Text.Json;
using Convy.Services.Downloaders.Slskd;
using Convy.Services.Downloads;
using Convy.Sources;
using Convy.Sources.Slskd;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class SoulseekPathsTests
{
    [Fact]
    public void SplitsRemoteNames()
    {
        const string file = @"@@abcde\Music\Evanescence\2003 - Fallen\CD1\01.flac";

        Assert.Equal(@"@@abcde\Music\Evanescence\2003 - Fallen\CD1", SoulseekPaths.DirectoryOf(file));
        Assert.Equal("01.flac", SoulseekPaths.FileNameOf(file));
        Assert.Equal("CD1/01.flac", SoulseekPaths.RelativeTo(@"@@abcde\Music\Evanescence\2003 - Fallen", file));
        Assert.Equal(file, SoulseekPaths.Combine(@"@@abcde\Music\Evanescence\2003 - Fallen", "CD1/01.flac"));
    }

    [Theory]
    [InlineData(@"@@abcde\Music\Album\01.flac", "Album/01.flac")]
    [InlineData(@"@@abcde\01.flac", "01.flac")]               // share root: no folder
    [InlineData(@"@@abcde\AC/DC\01.flac", "AC_DC/01.flac")]   // '/' is not allowed in a local name
    public void LocalPathFollowsSlskdDefaultLayout(string remote, string local) =>
        Assert.Equal(local, SoulseekPaths.LocalPathOf(remote));

    [Fact]
    public void ItemRefRoundTripsAnyUserAndFolder()
    {
        var itemRef = SoulseekPaths.ItemRef("bob/the builder", @"@@abc12\Music\A&B");

        Assert.True(SoulseekPaths.TryParseItemRef(itemRef, out var user, out var directory));
        Assert.Equal("bob/the builder", user);
        Assert.Equal(@"@@abc12\Music\A&B", directory);
        Assert.False(SoulseekPaths.TryParseItemRef("0123456789abcdef", out _, out _));
    }
}

internal sealed class FakeSlskd : HttpMessageHandler
{
    public List<(string Method, string Path, string? Body)> Requests { get; } = [];
    public Func<string, string, string?, HttpResponseMessage> Respond { get; set; } = (_, _, _) => Json("[]");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.PathAndQuery;
        lock (Requests) Requests.Add((request.Method.Method, path, body));
        return Respond(request.Method.Method, path, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static SlskdClient Client(FakeSlskd handler, string? downloadsPath = "/data/slskd") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://slskd:5030/api/v0/") },
            new SlskdOptions { Url = "http://slskd:5030", ApiKey = "k", DownloadsPath = downloadsPath, SearchMaxSeconds = 5 });
}

public class SlskdDownloaderTests
{
    private const string Dir = @"@@abcde\Music\Evanescence\2003 - Fallen";
    private readonly FakeTime _time = new();

    private SlskdDownloader Create(FakeSlskd handler) =>
        new(FakeSlskd.Client(handler), _time, NullLogger<SlskdDownloader>.Instance);

    private static SlskdTransfer T(string file, string state, long size = 100, long done = 0, DateTimeOffset? retry = null, string? error = null) => new()
    {
        Id = Guid.NewGuid(),
        Username = "bob",
        Filename = Dir + "\\" + file,
        Size = size,
        State = state,
        BytesTransferred = done,
        NextAttemptAt = retry,
        Exception = error,
    };

    [Fact]
    public void AllSucceededIsCompletedWithLocalPaths()
    {
        var item = Create(new FakeSlskd()).ToItem("bob", Dir,
            [T("01.flac", "Completed, Succeeded"), T("02.flac", "Completed, Succeeded")], "/data/slskd");

        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal("2003 - Fallen", item.Name);
        Assert.Equal(["2003 - Fallen/01.flac", "2003 - Fallen/02.flac"], item.Files.Select(f => f.Path));
        Assert.True(item.Files.All(f => f.IsComplete));
        Assert.Equal("bob", item.Properties["Username"]);
        Assert.False(item.Properties.ContainsKey("Tags"));
        Assert.Equal(SoulseekPaths.ItemRef("bob", Dir), item.ItemRef);
    }

    [Fact]
    public void FinalFailuresFailTheFolderListingTheFiles()
    {
        var item = Create(new FakeSlskd()).ToItem("bob", Dir,
            [T("01.flac", "Completed, Succeeded"), T("02.flac", "Completed, Rejected", error: "File not shared")], "/d");

        Assert.Equal(DownloadState.Failed, item.State);
        Assert.Contains("1 file(s) were not downloaded", item.Error);
        Assert.Contains("02.flac (Rejected: File not shared)", item.Error);
    }

    [Fact]
    public void FailureWithAPendingRetryIsNotFinal()
    {
        var item = Create(new FakeSlskd()).ToItem("bob", Dir,
            [T("01.flac", "Completed, Succeeded"), T("02.flac", "Completed, Errored", retry: _time.Now.AddMinutes(1))], "/d");

        Assert.Equal(DownloadState.Downloading, item.State);
    }

    [Theory]
    [InlineData("Queued, Remotely", DownloadState.Queued)]
    [InlineData("Requested", DownloadState.Queued)]
    [InlineData("InProgress", DownloadState.Downloading)]
    [InlineData("Initializing", DownloadState.Downloading)]
    public void MapsTransferStates(string state, DownloadState expected) =>
        Assert.Equal(expected, Create(new FakeSlskd()).ToItem("bob", Dir, [T("01.flac", state, done: 10)], "/d").State);

    [Fact]
    public async Task AddQueuesOnlySelectedFilesAndReturnsTheFolderRef()
    {
        var http = new FakeSlskd { Respond = (_, _, _) => FakeSlskd.Json("""{"enqueued":[{"id":"6f1d6c16-9c1b-4a8a-9c3f-8f7d9d1e2a3b","username":"bob","filename":"x"}],"failed":[]}""", HttpStatusCode.Created) };
        var payload = new SoulseekPayload("bob", Dir,
            [new SoulseekFile(Dir + @"\01.flac", 10), new SoulseekFile(Dir + @"\cover.jpg", 1), new SoulseekFile(Dir + @"\CD2\01.flac", 12)]);

        var itemRef = await Create(http).AddAsync(payload, new FileSelection(["01.flac", "CD2/01.flac"]), new AddOptions("Music"), CancellationToken.None);

        var (method, path, body) = Assert.Single(http.Requests);
        Assert.Equal("POST", method);
        Assert.Equal("/api/v0/transfers/downloads/bob", path);
        var files = JsonDocument.Parse(body!).RootElement.EnumerateArray().Select(e => e.GetProperty("filename").GetString()).ToList();
        Assert.Equal([Dir + @"\01.flac", Dir + @"\CD2\01.flac"], files);
        Assert.Equal(SoulseekPaths.ItemRef("bob", Dir), itemRef);
    }

    [Fact]
    public async Task AddFailsWhenNothingWasQueued()
    {
        var http = new FakeSlskd { Respond = (_, _, _) => FakeSlskd.Json("""{"enqueued":[],"failed":[{}]}""", HttpStatusCode.Created) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(http).AddAsync(
            new SoulseekPayload("bob", Dir, [new SoulseekFile(Dir + @"\01.flac", 10)]), FileSelection.All, new AddOptions(null), CancellationToken.None));
    }

    [Fact]
    public async Task ItemsAreGroupedByUserAndFolder()
    {
        var http = new FakeSlskd
        {
            Respond = (_, _, _) => FakeSlskd.Json($$"""
                [{"username":"bob","directories":[{"directory":"ignored","files":[
                  {"id":"{{Guid.NewGuid()}}","username":"bob","filename":"{{Dir.Replace("\\", "\\\\")}}\\01.flac","size":5,"state":"Completed, Succeeded"},
                  {"id":"{{Guid.NewGuid()}}","username":"bob","filename":"@@abcde\\Other\\x.mp3","size":5,"state":"Queued, Remotely"}]}]}]
                """),
        };

        var items = await Create(http).GetItemsAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.ItemRef == SoulseekPaths.ItemRef("bob", Dir) && i.State == DownloadState.Completed);
        Assert.All(items, i => Assert.Equal("/data/slskd", i.SavePath));
    }

    [Fact]
    public async Task CancelStopsOnlyUnfinishedTransfersOfTheFolder()
    {
        var running = Guid.NewGuid();
        var http = new FakeSlskd
        {
            Respond = (method, _, _) => method == "GET"
                ? FakeSlskd.Json($$"""
                    {"username":"bob","directories":[{"directory":"d","files":[
                      {"id":"{{running}}","username":"bob","filename":"{{Dir.Replace("\\", "\\\\")}}\\01.flac","size":5,"state":"InProgress"},
                      {"id":"{{Guid.NewGuid()}}","username":"bob","filename":"{{Dir.Replace("\\", "\\\\")}}\\02.flac","size":5,"state":"Completed, Succeeded"}]}]}
                    """)
                : new HttpResponseMessage(HttpStatusCode.NoContent),
        };

        await Create(http).CancelAsync(SoulseekPaths.ItemRef("bob", Dir), CancellationToken.None);

        var delete = Assert.Single(http.Requests, r => r.Method == "DELETE");
        Assert.Equal($"/api/v0/transfers/downloads/bob/{running}?remove=false", delete.Path);
    }
}

public class SoulseekSourceTests
{
    private static readonly CategorySearchSettings Music = new("music", [], ["flac"]);

    [Fact]
    public async Task SearchGroupsHitsByUserFolderAndFiltersExtensions()
    {
        var http = new FakeSlskd
        {
            Respond = (method, path, _) => (method, path) switch
            {
                ("POST", "/api/v0/searches") => FakeSlskd.Json("""{"id":"11111111-1111-1111-1111-111111111111","state":"InProgress","isComplete":false}"""),
                ("GET", var p) when p.EndsWith("/responses") => FakeSlskd.Json("""
                    [{"username":"bob","hasFreeUploadSlot":true,"queueLength":0,"uploadSpeed":1048576,"files":[
                        {"filename":"@@abcde\\Music\\Evanescence\\2003 - Fallen\\01.flac","size":30},
                        {"filename":"@@abcde\\Music\\Evanescence\\2003 - Fallen\\02.flac","size":40},
                        {"filename":"@@abcde\\Music\\Evanescence\\2003 - Fallen\\cover.jpg","size":1}]},
                     {"username":"eve","hasFreeUploadSlot":false,"queueLength":12,"uploadSpeed":10,"files":[
                        {"filename":"@@zzzzz\\mp3\\Fallen\\01.mp3","size":5}]}]
                    """),
                ("GET", _) => FakeSlskd.Json("""{"id":"11111111-1111-1111-1111-111111111111","state":"Completed, TimedOut","isComplete":true}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            },
        };
        var source = new SoulseekSource(FakeSlskd.Client(http), SourceStatus.Ok, null, NullLogger<SoulseekSource>.Instance);

        var results = new List<ContentInfo>();
        await foreach (var c in source.SearchAsync(new SearchRequest("evanescence fallen", Music), CancellationToken.None))
            results.Add(c);

        var album = Assert.Single(results);
        Assert.Equal("Evanescence / 2003 - Fallen [flac] — bob", album.Title);
        Assert.Equal(2, album.FileCount);
        Assert.Equal(70, album.SizeBytes);
        Assert.True(album.Availability.FreeUploadSlot);
        Assert.StartsWith("slsk:bob", album.DedupKey);
        Assert.Contains(http.Requests, r => r.Method == "DELETE" && r.Path.StartsWith("/api/v0/searches/"));

        var searchBody = JsonDocument.Parse(http.Requests.First(r => r.Method == "POST").Body!).RootElement;
        Assert.Equal("evanescence fallen", searchBody.GetProperty("searchText").GetString());
        Assert.Equal(8000, searchBody.GetProperty("searchTimeout").GetInt32()); // milliseconds
    }

    [Fact]
    public async Task ListsTheWholeFolderAndFallsBackToSearchHits()
    {
        const string dir = @"@@abcde\Music\Album";
        var contentId = SoulseekContentRef.Serialize(new SoulseekContentRef("bob", dir, [new SlskdEnqueueFile(dir + @"\01.flac", 3)]));
        var browseFails = false;
        var http = new FakeSlskd
        {
            Respond = (_, _, _) => browseFails
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("User bob is offline") }
                : FakeSlskd.Json("""
                    [{"name":"@@abcde\\Music\\Album","files":[{"filename":"01.flac","size":3},{"filename":"cover.jpg","size":1}]},
                     {"name":"@@abcde\\Music\\Album\\Scans","files":[{"filename":"back.png","size":2}]}]
                    """),
        };
        var source = new SoulseekSource(FakeSlskd.Client(http), SourceStatus.Ok, null, NullLogger<SoulseekSource>.Instance);

        var listing = await source.ListFilesAsync(contentId, CancellationToken.None);
        // Subfolders are separate results: slskd stores them in separate local folders.
        Assert.Equal(["01.flac", "cover.jpg"], listing.Files.Select(f => f.Path));

        var payload = Assert.IsType<SoulseekPayload>(await source.ResolveAsync(contentId, CancellationToken.None));
        Assert.Equal(dir + @"\cover.jpg", payload.Files[^1].Filename);

        browseFails = true;
        Assert.Equal(["01.flac"], (await source.ListFilesAsync(contentId, CancellationToken.None)).Files.Select(f => f.Path));
    }

    [Fact]
    public async Task ProviderReportsWhenSlskdIsNotLoggedIn()
    {
        var http = new FakeSlskd { Respond = (_, _, _) => FakeSlskd.Json("""{"state":"Disconnected","isLoggedIn":false}""") };
        var provider = new SoulseekSourceProvider(FakeSlskd.Client(http), NullLogger<SoulseekSource>.Instance);

        var source = Assert.Single(await provider.GetSourcesAsync(CancellationToken.None));

        Assert.Equal("soulseek", source.Id);
        Assert.Equal(SourceStatus.Error, source.Status);
    }
}
