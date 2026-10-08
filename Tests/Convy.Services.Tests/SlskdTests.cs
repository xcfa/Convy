using System.Net;
using System.Text;
using System.Text.Json;
using Convy.Services.Downloaders.Slskd;
using Convy.Services.Downloads;
using Convy.Services.Placement;
using Convy.Sources;
using Convy.Sources.Slskd;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class SoulseekPathsTests
{
    private const string Album = @"@@abcde\Music\Evanescence\2003 - Fallen";

    [Fact]
    public void SplitsRemoteNames()
    {
        const string file = Album + @"\CD1\01.flac";

        Assert.Equal(Album + @"\CD1", SoulseekPaths.DirectoryOf(file));
        Assert.Equal("01.flac", SoulseekPaths.FileNameOf(file));
        Assert.Equal("CD1/01.flac", SoulseekPaths.RelativeTo(Album, file));
        Assert.Equal(file, SoulseekPaths.Combine(Album, "CD1/01.flac"));
        Assert.True(SoulseekPaths.IsUnder(Album + @"\CD1", Album));
        Assert.False(SoulseekPaths.IsUnder(Album + " (Remaster)", Album));
    }

    [Theory]
    [InlineData(@"@@abcde\Music\Album\01.flac", "Album/01.flac")]
    [InlineData(@"@@abcde\01.flac", "01.flac")]               // share root: no folder
    [InlineData(@"@@abcde\AC/DC\01.flac", "AC_DC/01.flac")]   // '/' is not allowed in a local name
    public void ManualDownloadsFollowSlskdDefaultLayout(string remote, string local) =>
        Assert.Equal(local, SoulseekPaths.LocalPathOf(remote));

    [Fact]
    public void DestinationKeepsSubfoldersUnderAFolderKey()
    {
        var key = SoulseekPaths.FolderKey("bob", Album);

        Assert.Equal(12, key.Length);
        Assert.Equal($"convy/{key}/2003 - Fallen", SoulseekPaths.DestinationOf("bob", Album, ""));
        Assert.Equal($"convy/{key}/2003 - Fallen/CD2", SoulseekPaths.DestinationOf("bob", Album, "CD2"));
        Assert.Equal($"convy/{key}/2003 - Fallen/_/x", SoulseekPaths.DestinationOf("bob", Album, "../x"));
        Assert.NotEqual(key, SoulseekPaths.FolderKey("eve", Album));
    }

    [Theory]
    [InlineData("", Album + @"\01.flac", "2003 - Fallen/01.flac")]
    [InlineData("CD2", Album + @"\CD2\01.flac", "2003 - Fallen/CD2/01.flac")]
    public void DestinationParsesBackToTheFolderAndItemPath(string subfolder, string file, string itemPath)
    {
        var destination = SoulseekPaths.DestinationOf("bob", Album, subfolder);

        Assert.True(SoulseekPaths.TryParseDestination(destination, "bob", file, out var directory, out var path));
        Assert.Equal(Album, directory);
        Assert.Equal(itemPath, path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("music/inbox")]
    [InlineData("convy/000000000000/2003 - Fallen")] // key of another folder
    public void ForeignDestinationsAreNotConvys(string? destination) =>
        Assert.False(SoulseekPaths.TryParseDestination(destination, "bob", Album + @"\01.flac", out _, out _));

    [Theory]
    [InlineData(@"x\Album\CD1", true)]
    [InlineData(@"x\Album\Disc 2", true)]
    [InlineData(@"x\Album\cd03 bonus", true)]
    [InlineData(@"x\Album\Scans", false)]
    [InlineData(@"x\CDs I like", false)]
    public void RecognisesDiscFolders(string directory, bool expected) =>
        Assert.Equal(expected, SoulseekPaths.IsDiscFolder(directory));

    [Fact]
    public void ItemRefsRoundTripAndKeepManualDownloadsApart()
    {
        var itemRef = SoulseekPaths.ItemRef("bob/the builder", @"@@abc12\Music\A&B");
        var manual = SoulseekPaths.ManualItemRef("bob/the builder", @"@@abc12\Music\A&B");

        Assert.True(SoulseekPaths.TryParseItemRef(itemRef, out var user, out var directory, out var isManual));
        Assert.Equal(("bob/the builder", @"@@abc12\Music\A&B", false), (user, directory, isManual));

        Assert.True(SoulseekPaths.TryParseItemRef(manual, out user, out directory, out isManual));
        Assert.Equal(("bob/the builder", @"@@abc12\Music\A&B", true), (user, directory, isManual));

        Assert.NotEqual(itemRef, manual);
        Assert.False(SoulseekPaths.TryParseItemRef("0123456789abcdef", out _, out _, out _));
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

    public static string Escape(string value) => JsonSerializer.Serialize(value)[1..^1];
}

public class SlskdDownloaderTests
{
    private const string Dir = @"@@abcde\Music\Evanescence\2003 - Fallen";
    private static readonly Guid RootBatch = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid DiscBatch = Guid.Parse("11111111-0000-0000-0000-000000000002");

    private readonly FakeTime _time = new();

    private SlskdDownloader Create(FakeSlskd handler) =>
        new(FakeSlskd.Client(handler), _time, NullLogger<SlskdDownloader>.Instance);

    private static SlskdTransfer T(string file, string state, long size = 100, long done = 0, DateTimeOffset? retry = null,
        string? error = null, Guid? batch = null) => new()
    {
        Id = Guid.NewGuid(),
        Username = "bob",
        Filename = Dir + "\\" + file,
        Size = size,
        State = state,
        BytesTransferred = done,
        NextAttemptAt = retry,
        Exception = error,
        BatchId = batch,
    };

    private static string TransferJson(SlskdTransfer t) =>
        $$"""{"id":"{{t.Id}}","username":"bob","filename":"{{FakeSlskd.Escape(t.Filename)}}","size":{{t.Size}},"state":"{{t.State}}"{{(t.BatchId is { } b ? $",\"batchId\":\"{b}\"" : "")}}{{(t.Exception is { } e ? $",\"exception\":\"{e}\"" : "")}}}""";

    /// <summary>slskd answering transfer and batch queries for the given transfers.</summary>
    private static FakeSlskd Slskd(params SlskdTransfer[] transfers)
    {
        var files = string.Join(",", transfers.Select(TransferJson));
        return new FakeSlskd
        {
            Respond = (method, path, _) => (method, path) switch
            {
                ("GET", "/api/v0/transfers/downloads") => FakeSlskd.Json($$"""[{"username":"bob","directories":[{"directory":"x","files":[{{files}}]}]}]"""),
                ("GET", "/api/v0/transfers/downloads/bob") => FakeSlskd.Json($$"""{"username":"bob","directories":[{"directory":"x","files":[{{files}}]}]}"""),
                ("GET", var p) when p.EndsWith(RootBatch.ToString()) =>
                    FakeSlskd.Json($$$"""{"id":"{{{RootBatch}}}","options":{"destination":"{{{SoulseekPaths.DestinationOf("bob", Dir, "")}}}"}}"""),
                ("GET", var p) when p.EndsWith(DiscBatch.ToString()) =>
                    FakeSlskd.Json($$$"""{"id":"{{{DiscBatch}}}","options":{"destination":"{{{SoulseekPaths.DestinationOf("bob", Dir, "CD2")}}}"}}"""),
                ("GET", var p) when p.Contains("/batches/") => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            },
        };
    }

    private async Task<DownloadItem> Item(params SlskdTransfer[] transfers) =>
        Assert.Single(await Create(Slskd(transfers)).GetItemsAsync(CancellationToken.None));

    [Fact]
    public async Task ConvyDownloadKeepsItsSubfoldersInOneItem()
    {
        var item = await Item(
            T("01.flac", "Completed, Succeeded", batch: RootBatch),
            T(@"CD2\01.flac", "Completed, Succeeded", batch: DiscBatch));

        var key = SoulseekPaths.FolderKey("bob", Dir);
        Assert.Equal(SoulseekPaths.ItemRef("bob", Dir), item.ItemRef);
        Assert.Equal(DownloadState.Completed, item.State);
        Assert.Equal(Path.Combine("/data/slskd", "convy", key), item.SavePath);
        Assert.Equal(["2003 - Fallen/01.flac", "2003 - Fallen/CD2/01.flac"], item.Files.Select(f => f.Path));
        Assert.Equal("2003 - Fallen", PlacementPlanner.FindRoot(item.Files));
        Assert.Equal("bob", item.Properties["Username"]);
        Assert.False(item.Properties.ContainsKey("Tags"));
    }

    [Fact]
    public async Task ManualDownloadsUseTheDefaultLayout()
    {
        var item = await Item(T("01.flac", "Completed, Succeeded"));

        Assert.Equal(SoulseekPaths.ManualItemRef("bob", Dir), item.ItemRef);
        Assert.Equal("/data/slskd", item.SavePath);
        Assert.Equal("2003 - Fallen/01.flac", Assert.Single(item.Files).Path);
    }

    [Fact]
    public async Task FinalFailuresFailTheItemListingTheFiles()
    {
        var item = await Item(
            T("01.flac", "Completed, Succeeded", batch: RootBatch),
            T("02.flac", "Completed, Rejected", error: "File not shared", batch: RootBatch));

        Assert.Equal(DownloadState.Failed, item.State);
        Assert.Contains("1 file(s) were not downloaded", item.Error);
        Assert.Contains("02.flac (Rejected: File not shared)", item.Error);
    }

    [Fact]
    public void FailureWithAPendingRetryIsNotFinal()
    {
        var downloader = Create(new FakeSlskd());
        var item = downloader.ToItem(
        [
            new PlacedTransfer(T("01.flac", "Completed, Succeeded"), "r", Dir, "/d", "a/01.flac"),
            new PlacedTransfer(T("02.flac", "Completed, Errored", retry: _time.Now.AddMinutes(1)), "r", Dir, "/d", "a/02.flac"),
        ]);

        Assert.Equal(DownloadState.Downloading, item.State);
    }

    [Theory]
    [InlineData("Queued, Remotely", DownloadState.Queued)]
    [InlineData("Requested", DownloadState.Queued)]
    [InlineData("InProgress", DownloadState.Downloading)]
    [InlineData("Initializing", DownloadState.Downloading)]
    public void MapsTransferStates(string state, DownloadState expected) =>
        Assert.Equal(expected, Create(new FakeSlskd())
            .ToItem([new PlacedTransfer(T("01.flac", state, done: 10), "r", Dir, "/d", "a/01.flac")]).State);

    [Fact]
    public async Task AddQueuesOneBatchPerSubfolderWithItsDestination()
    {
        var http = new FakeSlskd
        {
            Respond = (_, _, body) => FakeSlskd.Json(
                $$"""{"batch":{"id":"{{Guid.NewGuid()}}","transfers":[{"id":"{{Guid.NewGuid()}}","username":"bob","filename":"x"}]},"failures":[]}""",
                HttpStatusCode.Created),
        };
        var payload = new SoulseekPayload("bob", Dir,
        [
            new SoulseekFile(Dir + @"\01.flac", 10),
            new SoulseekFile(Dir + @"\cover.jpg", 1),
            new SoulseekFile(Dir + @"\CD2\01.flac", 12),
        ]);

        var itemRef = await Create(http).AddAsync(
            payload, new FileSelection(["01.flac", "CD2/01.flac"]), new AddOptions("Music"), CancellationToken.None);

        Assert.Equal(SoulseekPaths.ItemRef("bob", Dir), itemRef);
        Assert.All(http.Requests, r => Assert.Equal(("POST", "/api/v0/transfers/downloads/batches"), (r.Method, r.Path)));

        var batches = http.Requests.Select(r => JsonDocument.Parse(r.Body!).RootElement).ToList();
        Assert.Equal(2, batches.Count);
        Assert.Equal(SoulseekPaths.DestinationOf("bob", Dir, ""), batches[0].GetProperty("options").GetProperty("destination").GetString());
        Assert.Equal([Dir + @"\01.flac"], batches[0].GetProperty("files").EnumerateArray().Select(f => f.GetProperty("filename").GetString()));
        Assert.Equal(SoulseekPaths.DestinationOf("bob", Dir, "CD2"), batches[1].GetProperty("options").GetProperty("destination").GetString());
        Assert.Equal("bob", batches[1].GetProperty("username").GetString());
    }

    [Fact]
    public async Task AddFailsWhenNothingWasQueued()
    {
        var http = new FakeSlskd
        {
            Respond = (_, _, _) => FakeSlskd.Json("""{"batch":{"transfers":[]},"failures":[{"filename":"a\\01.flac","message":"User is offline"}]}"""),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(http).AddAsync(
            new SoulseekPayload("bob", Dir, [new SoulseekFile(Dir + @"\01.flac", 10)]), FileSelection.All, new AddOptions(null), CancellationToken.None));

        Assert.Contains("User is offline", ex.Message);
    }

    [Fact]
    public async Task OldSlskdIsReportedClearly()
    {
        var http = new FakeSlskd { Respond = (_, _, _) => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("The JSON value could not be converted") } };

        var ex = await Assert.ThrowsAsync<SourceException>(() => Create(http).AddAsync(
            new SoulseekPayload("bob", Dir, [new SoulseekFile(Dir + @"\01.flac", 10)]), FileSelection.All, new AddOptions(null), CancellationToken.None));

        Assert.Contains("0.26", ex.Message);
    }

    [Fact]
    public async Task CancelStopsOnlyUnfinishedTransfersOfTheItem()
    {
        var running = T("01.flac", "InProgress", batch: RootBatch);
        var done = T("02.flac", "Completed, Succeeded", batch: RootBatch);
        var manual = T("03.flac", "InProgress");
        var http = Slskd(running, done, manual);

        await Create(http).CancelAsync(SoulseekPaths.ItemRef("bob", Dir), CancellationToken.None);

        var delete = Assert.Single(http.Requests, r => r.Method == "DELETE");
        Assert.Equal($"/api/v0/transfers/downloads/bob/{running.Id}?remove=false", delete.Path);
    }

    [Fact]
    public async Task BatchDestinationsAreLookedUpOnce()
    {
        var http = Slskd(T("01.flac", "InProgress", batch: RootBatch), T("02.flac", "InProgress", batch: RootBatch));
        var downloader = Create(http);

        await downloader.GetItemsAsync(CancellationToken.None);
        await downloader.GetItemsAsync(CancellationToken.None);

        Assert.Single(http.Requests, r => r.Path.Contains("/batches/"));
    }
}

public class SoulseekSourceTests
{
    private static readonly CategorySearchSettings Music = new("music", [], ["flac"]);

    private static FakeSlskd Search(string responses) => new()
    {
        Respond = (method, path, _) => (method, path) switch
        {
            ("POST", "/api/v0/searches") => FakeSlskd.Json("""{"id":"11111111-1111-1111-1111-111111111111","state":"InProgress","isComplete":false}"""),
            ("GET", var p) when p.EndsWith("/responses") => FakeSlskd.Json(responses),
            ("GET", _) => FakeSlskd.Json("""{"id":"11111111-1111-1111-1111-111111111111","state":"Completed, TimedOut","isComplete":true}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        },
    };

    private static async Task<List<ContentInfo>> Run(FakeSlskd http, string query = "evanescence fallen")
    {
        var source = new SoulseekSource(FakeSlskd.Client(http), SourceStatus.Ok, null, NullLogger<SoulseekSource>.Instance);
        var results = new List<ContentInfo>();
        await foreach (var c in source.SearchAsync(new SearchRequest(query, Music), CancellationToken.None))
            results.Add(c);
        return results;
    }

    [Fact]
    public async Task SearchGroupsHitsByUserFolderAndFiltersExtensions()
    {
        var http = Search("""
            [{"username":"bob","hasFreeUploadSlot":true,"queueLength":0,"uploadSpeed":1048576,"files":[
                {"filename":"@@abcde\\Music\\Evanescence\\2003 - Fallen\\01.flac","size":30},
                {"filename":"@@abcde\\Music\\Evanescence\\2003 - Fallen\\02.flac","size":40},
                {"filename":"@@abcde\\Music\\Evanescence\\2003 - Fallen\\cover.jpg","size":1}]},
             {"username":"eve","hasFreeUploadSlot":false,"queueLength":12,"uploadSpeed":10,"files":[
                {"filename":"@@zzzzz\\mp3\\Fallen\\01.mp3","size":5}]}]
            """);

        var album = Assert.Single(await Run(http));

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
    public async Task DiscFoldersFormOneRelease()
    {
        var http = Search("""
            [{"username":"bob","hasFreeUploadSlot":true,"queueLength":0,"uploadSpeed":1,"files":[
                {"filename":"@@abcde\\Music\\Album\\CD1\\01.flac","size":30},
                {"filename":"@@abcde\\Music\\Album\\CD2\\01.flac","size":40}]}]
            """);

        var album = Assert.Single(await Run(http));

        Assert.Equal(2, album.FileCount);
        Assert.Equal(@"slsk:bob" + "\n" + @"@@abcde\Music\Album", album.DedupKey);
    }

    [Fact]
    public async Task ListsTheFolderWithSubfoldersAndSearchHits()
    {
        const string dir = @"@@abcde\Music\Album";
        var contentId = SoulseekContentRef.Serialize(new SoulseekContentRef("bob", dir,
            [new SlskdEnqueueFile(dir + @"\01.flac", 3), new SlskdEnqueueFile(dir + @"\CD2\01.flac", 4)]));
        var browseFails = false;
        var http = new FakeSlskd
        {
            Respond = (_, _, _) => browseFails
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("User bob is offline") }
                : FakeSlskd.Json("""
                    [{"name":"@@abcde\\Music\\Album","files":[{"filename":"01.flac","size":3},{"filename":"cover.jpg","size":1}]},
                     {"name":"@@abcde\\Music\\Album\\Scans","files":[{"filename":"back.png","size":2}]},
                     {"name":"@@abcde\\Music\\Album (Live)","files":[{"filename":"x.flac","size":2}]}]
                    """),
        };
        var source = new SoulseekSource(FakeSlskd.Client(http), SourceStatus.Ok, null, NullLogger<SoulseekSource>.Instance);

        var listing = await source.ListFilesAsync(contentId, CancellationToken.None);
        // The browsed folder, its subfolders, and search hits the browse did not return (CD2).
        Assert.Equal(["01.flac", "CD2/01.flac", "Scans/back.png", "cover.jpg"], listing.Files.Select(f => f.Path).Order(StringComparer.Ordinal));

        var payload = Assert.IsType<SoulseekPayload>(await source.ResolveAsync(contentId, CancellationToken.None));
        Assert.Contains(payload.Files, f => f.Filename == dir + @"\Scans\back.png");

        browseFails = true;
        Assert.Equal(["01.flac", "CD2/01.flac"], (await source.ListFilesAsync(contentId, CancellationToken.None)).Files.Select(f => f.Path));
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
