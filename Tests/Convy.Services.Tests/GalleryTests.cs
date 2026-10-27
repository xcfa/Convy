using System.IO.Compression;
using System.Net;
using System.Text;
using Convy.Services.Downloaders.Gallery;
using Convy.Services.Downloads;
using Convy.Services.External;
using Convy.Services.Jobs;
using Convy.Services.Media;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

/// <summary>
/// The gallery downloader over the test extension: it is loaded from its DLL, its site is <see cref="Web"/>,
/// the downloader and the REST service run against a real (in-memory SQLite) store.
/// </summary>
internal sealed class GalleryHarness : IDisposable
{
    /// <param name="captcha">The service of checks of the extensions; none by default.</param>
    public GalleryHarness(FakeCaptchaService? captcha = null)
    {
        Downloads = Directory.CreateTempSubdirectory("convy-gallery-").FullName;
        Host = Extensions.Host(Web, time: Time, captcha: captcha);
        Registry = new SourceRegistry([Host], Media.Health(), Time, NullLogger<SourceRegistry>.Instance);
        Registry.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        Store = new EfGalleryDownloadStore(Db);
        Downloader = new GalleryDownloader(Downloads, Store, Queue, Time, NullLogger<GalleryDownloader>.Instance);
        Runner = new GalleryDownloadRunner(Store, Registry, Queue, new StaticOptions<GalleryOptions>(Options), Time, NullLogger<GalleryDownloadRunner>.Instance);
        JobStore = new EfJobStore(Db);
        Jobs = new JobService(
            new DownloaderResolver([Downloader]), new FakeRules(), JobStore, new JobTransitions(JobStore, new RecordingJobEvents()),
            new FakeFileSystem(), new StaticOptions<JobOptions>(new JobOptions()), Time, NullLogger<JobService>.Instance);
        var categories = new Dictionary<string, CategoryOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["manga"] = new CategoryOptions { Description = "Manga", QbittorrentCategory = "Manga", Sources = ["test-gallery"] },
            ["other"] = new CategoryOptions { Description = "Other" },
        };
        Api = new GalleryApiService(
            Registry, Media.Catalog(categories), Jobs, JobStore, new DownloaderResolver([Downloader]),
            new StaticOptions<GalleryOptions>(Options), new StaticOptions<SearchOptions>(new SearchOptions { SourceTimeoutSeconds = 5 }));
    }

    public FakeSiteHandler Web { get; } = new();
    public FakeTime Time { get; } = new();
    public TestDb Db { get; } = new();
    public string Downloads { get; }
    public Convy.Services.Extensions.ExtensionHost Host { get; }
    public SourceRegistry Registry { get; }
    public GalleryOptions Options { get; } = new() { Category = "manga", ParallelPages = 2, PageTimeoutSeconds = 10 };
    public EfGalleryDownloadStore Store { get; }
    public GalleryDownloadQueue Queue { get; } = new();
    public GalleryDownloader Downloader { get; }
    public GalleryDownloadRunner Runner { get; }
    public EfJobStore JobStore { get; }
    public JobService Jobs { get; }
    public GalleryApiService Api { get; }

    /// <summary>The release of a gallery on the fake site: its address.</summary>
    public static string Release(string id) => $"https://gallery.test/g/{id}";

    /// <summary>
    /// A gallery <paramref name="id"/> on the fake site with <paramref name="pages"/> pages whose images are <c>page N</c>;
    /// the listing names the first image when <paramref name="listed"/>, the others are asked for per page.
    /// </summary>
    public void Gallery(string id, string title, int pages, string imageHost = "img.gallery.test", string folder = "img", bool listed = true)
    {
        Web.Json($"https://gallery.test/api/g/{id}", $$"""{ "id": "{{id}}", "title": "{{title}}", "pages": {{pages}}, "tags": ["female:glasses", "full color"], "artists": ["Someone"], "language": "en" }""");
        Web.Json($"https://gallery.test/api/g/{id}/pages", "[" + string.Join(",", Enumerable.Range(1, pages).Select(n => n == 1 && listed ? $$"""{ "image": "https://{{imageHost}}/{{folder}}/{{id}}/1.jpg" }""" : "{}")) + "]");
        for (var n = 1; n <= pages; n++)
        {
            Web.Json($"https://gallery.test/api/g/{id}/{n}", $$"""{ "image": "https://{{imageHost}}/{{folder}}/{{id}}/{{n}}.jpg" }""");
            Web.Bytes($"https://img.gallery.test/img/{id}/{n}.jpg", System.Text.Encoding.ASCII.GetBytes($"page {n}"));
        }
    }

    public async Task<string> AddAsync(string id, string title = "Title", int pages = 3)
    {
        Gallery(id, title, pages);
        return await Downloader.AddAsync(new GalleryPayload("test-gallery", Release(id), title, pages), FileSelection.All, new AddOptions("Manga"), CancellationToken.None);
    }

    public void Dispose()
    {
        Host.Dispose();
        Registry.Dispose();
        Db.Dispose();
        try
        {
            Directory.Delete(Downloads, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class GalleryDownloaderTests
{
    [Fact]
    public async Task DownloadsThePagesIntoACbzAndReportsTheItem()
    {
        using var h = new GalleryHarness();
        var itemRef = await h.AddAsync("g1", "My Gallery: Vol. 1");
        Assert.Equal("test-gallery:https://gallery.test/g/g1", itemRef);
        Assert.Equal(DownloadState.Queued, (await h.Downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);

        await h.Runner.RunAsync(itemRef, CancellationToken.None);

        var item = (await h.Downloader.GetItemAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(DownloadState.Completed, item.State);
        var file = Assert.Single(item.Files);
        Assert.Equal("My Gallery_ Vol. 1/My Gallery_ Vol. 1.cbz", file.Path);
        Assert.True(file.IsComplete);
        Assert.Equal("My Gallery_ Vol. 1", item.Properties["Name"]);
        Assert.Equal("Manga", item.Properties["Category"]);
        Assert.Equal(item.Size, file.Size);

        var archive = Path.Combine(item.SavePath, file.Path);
        Assert.True(File.Exists(archive));
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal(["0001.jpg", "0002.jpg", "0003.jpg"], zip.Entries.Select(e => e.Name).Order());
        using var reader = new StreamReader(zip.GetEntry("0002.jpg")!.Open());
        Assert.Equal("page 2", reader.ReadToEnd());
        Assert.False(Directory.Exists(Path.Combine(item.SavePath, ".pages")));

        // The image named in the listing was taken as is; the others were asked for per page.
        Assert.DoesNotContain("https://gallery.test/api/g/g1/1", h.Web.Requested);
        Assert.Contains("https://gallery.test/api/g/g1/2", h.Web.Requested);
    }

    [Fact]
    public async Task ABrokenImageIsFetchedFromTheRetryAddress()
    {
        using var h = new GalleryHarness();
        h.Gallery("g2", "Two", 2, folder: "broken", listed: false);
        h.Web.Bytes("https://img.gallery.test/img/g2/1.jpg", "page 1"u8.ToArray());
        h.Web.Bytes("https://img.gallery.test/img/g2/2.jpg", "page 2"u8.ToArray());
        h.Web.Status("https://img.gallery.test/broken/g2/1.jpg", HttpStatusCode.InternalServerError);
        h.Web.Status("https://img.gallery.test/broken/g2/2.jpg", HttpStatusCode.InternalServerError);
        var itemRef = await h.Downloader.AddAsync(new GalleryPayload("test-gallery", GalleryHarness.Release("g2"), "Two", 2), FileSelection.All, new AddOptions(null), CancellationToken.None);

        await h.Runner.RunAsync(itemRef, CancellationToken.None);

        var download = (await h.Store.GetAsync(itemRef, CancellationToken.None))!;
        Assert.Equal((GalleryDownloadState.Completed, 2, 2), (download.State, download.PagesDone, download.PagesTotal));
        Assert.Contains("https://img.gallery.test/img/g2/2.jpg", h.Web.Requested);
    }

    [Fact]
    public async Task AnArchiveTheSiteMakesIsKeptAsItComesInsteadOfThePages()
    {
        using var h = new GalleryHarness();
        h.Gallery("g3", "Three", 3);
        byte[] made;
        using (var buffer = new MemoryStream())
        {
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in new[] { "Three/01.webp", "Three/02.webp" })
                {
                    using var entry = new StreamWriter(zip.CreateEntry(name).Open());
                    entry.Write(name);
                }
            }

            made = buffer.ToArray();
        }

        h.Web.Json("https://gallery.test/api/g/g3/archive", """{ "url": "https://files.gallery.test/g3.cbz" }""");
        h.Web.Bytes("https://files.gallery.test/g3.cbz", made, "application/zip");
        var itemRef = await h.Downloader.AddAsync(new GalleryPayload("test-gallery", GalleryHarness.Release("g3"), "Three", 3), FileSelection.All, new AddOptions(null), CancellationToken.None);

        await h.Runner.RunAsync(itemRef, CancellationToken.None);

        var download = (await h.Store.GetAsync(itemRef, CancellationToken.None))!;
        // The site's own archive, its pages counted: two, not the three the gallery said.
        Assert.Equal((GalleryDownloadState.Completed, 2, 2, (long)made.Length), (download.State, download.PagesDone, download.PagesTotal, download.Bytes));
        Assert.Equal(made, await File.ReadAllBytesAsync(download.ArchivePath, CancellationToken.None));
        Assert.Equal("secret", FakeSiteHandler.Header(h.Web.Requests.Single(r => r.RequestUri!.AbsoluteUri == "https://files.gallery.test/g3.cbz"), "X-Token"));
        Assert.DoesNotContain(h.Web.Requested, r => r.StartsWith("https://gallery.test/api/g/g3/pages", StringComparison.Ordinal));
        Assert.False(File.Exists(download.ArchivePath + ".part"));
    }

    [Fact]
    public async Task AnArchiveThatIsNoZipFailsTheRunAndLeavesNothing()
    {
        using var h = new GalleryHarness();
        h.Gallery("g4", "Four", 1);
        h.Web.Json("https://gallery.test/api/g/g4/archive", """{ "url": "https://files.gallery.test/g4.cbz" }""");
        h.Web.Bytes("https://files.gallery.test/g4.cbz", "not a zip"u8.ToArray(), "application/zip");
        var itemRef = await h.Downloader.AddAsync(new GalleryPayload("test-gallery", GalleryHarness.Release("g4"), "Four", 1), FileSelection.All, new AddOptions(null), CancellationToken.None);

        await h.Runner.RunAsync(itemRef, CancellationToken.None);

        var download = (await h.Store.GetAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(GalleryDownloadState.Retrying, download.State);
        Assert.False(File.Exists(download.ArchivePath));
        Assert.False(File.Exists(download.ArchivePath + ".part"));
    }

    [Fact]
    public async Task AFailedPageWaitsForARetryKeepsTheFetchedPagesAndFailsForGood()
    {
        using var h = new GalleryHarness();
        var itemRef = await h.AddAsync("g3", pages: 3);
        h.Web.Status("https://img.gallery.test/img/g3/3.jpg", HttpStatusCode.NotFound);

        await h.Runner.RunAsync(itemRef, CancellationToken.None);

        var download = (await h.Store.GetAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(GalleryDownloadState.Retrying, download.State);
        Assert.Contains("Page 3", download.Error);
        Assert.Equal(h.Time.Now + TimeSpan.FromMinutes(1), download.NextAttemptAt);
        Assert.Equal(2, download.PagesDone);
        var item = (await h.Downloader.GetItemAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(DownloadState.Errored, item.State);
        Assert.InRange(item.Files[0].Progress, 0.6, 0.7);

        // The retry: the two pages on disk are not fetched again.
        var before = h.Web.Requested.Count(u => u == "https://img.gallery.test/img/g3/1.jpg");
        await h.Store.TryUpdateAsync(download with { State = GalleryDownloadState.Queued }, download.State, CancellationToken.None);
        await h.Runner.RunAsync(itemRef, CancellationToken.None);
        Assert.Equal(before, h.Web.Requested.Count(u => u == "https://img.gallery.test/img/g3/1.jpg"));
        download = (await h.Store.GetAsync(itemRef, CancellationToken.None))!;
        Assert.Equal((GalleryDownloadState.Retrying, 2), (download.State, download.Attempts));

        await h.Store.TryUpdateAsync(download with { State = GalleryDownloadState.Queued }, download.State, CancellationToken.None);
        await h.Runner.RunAsync(itemRef, CancellationToken.None);
        download = (await h.Store.GetAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(GalleryDownloadState.Failed, download.State);
        Assert.Equal(DownloadState.Failed, (await h.Downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);

        // The site fixed: a new job for the same release picks the pages up.
        h.Web.Bytes("https://img.gallery.test/img/g3/3.jpg", "page 3"u8.ToArray());
        await h.Downloader.AddAsync(new GalleryPayload("test-gallery", GalleryHarness.Release("g3"), "Title", 3), FileSelection.All, new AddOptions(null), CancellationToken.None);
        await h.Runner.RunAsync(itemRef, CancellationToken.None);
        Assert.Equal(GalleryDownloadState.Completed, (await h.Store.GetAsync(itemRef, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task ACancelledDownloadStaysCancelledAndASelectionIsRefused()
    {
        using var h = new GalleryHarness();
        var itemRef = await h.AddAsync("g4");

        await h.Downloader.CancelAsync(itemRef, CancellationToken.None);
        await h.Runner.RunAsync(itemRef, CancellationToken.None);

        Assert.Equal(DownloadState.Paused, (await h.Downloader.GetItemAsync(itemRef, CancellationToken.None))!.State);
        Assert.DoesNotContain(h.Web.Requested, u => u.Contains("/pages", StringComparison.Ordinal));

        await Assert.ThrowsAsync<ConvyRequestException>(() => h.Downloader.AddAsync(
            new GalleryPayload("test-gallery", GalleryHarness.Release("g4"), "Title"), new FileSelection(["a.jpg"]), new AddOptions(null), CancellationToken.None));
    }

    [Fact]
    public async Task AMissingSourceFailsTheDownload()
    {
        using var h = new GalleryHarness();
        var itemRef = await h.Downloader.AddAsync(new GalleryPayload("gone", "x", "Title"), FileSelection.All, new AddOptions(null), CancellationToken.None);

        await h.Runner.RunAsync(itemRef, CancellationToken.None);

        var download = (await h.Store.GetAsync(itemRef, CancellationToken.None))!;
        Assert.Equal(GalleryDownloadState.Failed, download.State);
        Assert.Contains("'gone' is not available", download.Error);
    }

    [Theory]
    [InlineData("A/B\\C: D?", "A_B_C_ D_")]
    [InlineData("  ...  ", "gallery")]
    [InlineData("Ünïcode 日本語", "Ünïcode 日本語")]
    public void NamesAreMadeSafeForTheFileSystem(string title, string expected) => Assert.Equal(expected, GalleryDownloader.SafeName(title));

    [Fact]
    public void LongNamesAreCutByBytesBetweenCharacters()
    {
        var japanese = GalleryDownloader.SafeName(new string('日', 150) + "!");
        Assert.Equal(66, japanese.Length); // 3 bytes each: 66 fit in 200
        Assert.True(Encoding.UTF8.GetByteCount(japanese) <= GalleryDownloader.MaxNameBytes);

        var emoji = GalleryDownloader.SafeName(string.Concat(Enumerable.Repeat("\U0001F600", 60)));
        Assert.True(Encoding.UTF8.GetByteCount(emoji) <= GalleryDownloader.MaxNameBytes);
        Assert.Equal(0, emoji.Length % 2); // never half a surrogate pair
        Assert.Equal(200, GalleryDownloader.SafeName(new string('a', 250)).Length);
    }
}

public class GalleryApiTests
{
    [Fact]
    public async Task ListsTheGallerySourcesWithTheirManifests()
    {
        using var h = new GalleryHarness();

        var source = Assert.Single(await h.Api.ListSourcesAsync(CancellationToken.None));

        Assert.Equal(("test-gallery", "Test gallery", "ok", true, 18), (source.Id, source.Name, source.Status, source.Nsfw, source.DefaultAgeRating));
        Assert.Equal(["gallery.test"], source.Domains);
        Assert.Equal(["manga", "doujinshi"], Assert.Single(source.Filters).Options.Select(o => o.Value));
    }

    [Fact]
    public async Task SearchesAndResolvesThroughTheSource()
    {
        using var h = new GalleryHarness();
        h.Web.Json("https://gallery.test/api/search?q=glasses&page=1&lang=en&category=manga",
            """{ "items": [ { "id": "g1", "title": "One", "pages": 3, "tags": ["female:glasses"] } ], "next": "2" }""");
        h.Gallery("g1", "One", 3);

        var page = await h.Api.SearchAsync("test-gallery", "glasses", "en", new Dictionary<string, IReadOnlyList<string>> { ["category"] = ["manga"] }, null, CancellationToken.None);
        Assert.Equal("b:2", page.NextPage);
        var item = Assert.Single(page.Items);
        // What the catalog says; the full metadata comes with the release's link.
        Assert.Equal(("https://gallery.test/g/g1", "One", "g1"), (item.ContentId, item.Title, item.Properties["id"]));

        var resolved = await h.Api.ResolveAsync("test-gallery", "https://gallery.test/g/g1", CancellationToken.None);
        Assert.Equal(("https://gallery.test/g/g1", "https://gallery.test/g/g1", "Someone"), (resolved.ContentId, resolved.InfoUrl, resolved.Properties["artists"]));

        var missing = await Assert.ThrowsAsync<ApiException>(() => h.Api.ResolveAsync("test-gallery", "https://gallery.test/g/nope", CancellationToken.None));
        Assert.Equal(404, missing.Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => h.Api.SearchAsync("prowlarr-1", "x", null, new Dictionary<string, IReadOnlyList<string>>(), null, CancellationToken.None))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => h.Api.ResolveAsync("test-gallery", "not a url", CancellationToken.None))).Status);
    }

    [Fact]
    public async Task StartsAJobFollowsItAndServesTheArchive()
    {
        using var h = new GalleryHarness();
        h.Gallery("g1", "One", 2);

        var job = await h.Api.StartJobAsync(new ApiStartJobRequest("test-gallery", GalleryHarness.Release("g1")), CancellationToken.None);
        Assert.Equal(("j_1", "queued", "One", 0, 2, false), (job.JobId, job.Status, job.Title, job.PagesDone, job.PagesTotal, job.ArchiveReady));
        var stored = Assert.Single(await h.JobStore.GetGroupAsync(1, CancellationToken.None));
        Assert.Equal(("gallery", "test-gallery:https://gallery.test/g/g1", "manga", "Manga", "https://gallery.test/g/g1"), (stored.Provider, stored.ItemRef, stored.Category, stored.ClientCategory, stored.OriginUrl));

        // Asked again while it runs: the same job, not a second one.
        var again = await h.Api.StartJobAsync(new ApiStartJobRequest("test-gallery", GalleryHarness.Release("g1")), CancellationToken.None);
        Assert.Equal("j_1", again.JobId);

        var notReady = await Assert.ThrowsAsync<ApiException>(() => h.Api.GetArchiveAsync("j_1", CancellationToken.None));
        Assert.Equal(404, notReady.Status);
        Assert.Contains("not ready yet (0/2", notReady.Message);

        await h.Runner.RunAsync(stored.ItemRef, CancellationToken.None);

        job = (await h.Api.GetJobAsync("j_1", CancellationToken.None))!;
        Assert.Equal((2, 2, true), (job.PagesDone, job.PagesTotal, job.ArchiveReady));
        Assert.NotNull(job.SizeBytes);
        var (path, fileName) = await h.Api.GetArchiveAsync("j_1", CancellationToken.None);
        Assert.Equal("One.cbz", fileName);
        Assert.Equal(job.SizeBytes, new FileInfo(path).Length);

        Assert.Null(await h.Api.GetJobAsync("j_9", CancellationToken.None));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => h.Api.GetArchiveAsync("j_9", CancellationToken.None))).Status);
    }

    [Fact]
    public async Task AJobNeedsAKnownSourceReleaseAndCategory()
    {
        using var h = new GalleryHarness();
        h.Gallery("g1", "One", 1);

        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => h.Api.StartJobAsync(new ApiStartJobRequest("", GalleryHarness.Release("g1")), CancellationToken.None))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => h.Api.StartJobAsync(new ApiStartJobRequest("slskd", GalleryHarness.Release("g1")), CancellationToken.None))).Status);
        var badCategory = await Assert.ThrowsAsync<ApiException>(() => h.Api.StartJobAsync(new ApiStartJobRequest("test-gallery", GalleryHarness.Release("g1"), "movies"), CancellationToken.None));
        Assert.Equal(400, badCategory.Status);
        Assert.Contains("Unknown category 'movies'", badCategory.Message);
        var noRelease = await Assert.ThrowsAsync<ApiException>(() => h.Api.StartJobAsync(new ApiStartJobRequest("test-gallery", GalleryHarness.Release("nope")), CancellationToken.None));
        Assert.Equal(502, noRelease.Status);
        Assert.Contains("no release at https://gallery.test/g/nope", noRelease.Message);

        var other = await h.Api.StartJobAsync(new ApiStartJobRequest("test-gallery", GalleryHarness.Release("g1"), "other"), CancellationToken.None);
        Assert.Equal("other", (await h.JobStore.GetGroupAsync(1, CancellationToken.None))[0].Category);
        Assert.Equal("j_1", other.JobId);
    }
}
