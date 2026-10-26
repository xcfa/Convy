using System.Net;
using System.Text;
using Convy.Services.Extensions;
using Convy.Sources;
using Convy.Sources.Torrents;

namespace Convy.Services.Tests;

/// <summary>Reads a .torrent as the bytes say: <c>torrent:&lt;hash&gt;</c>; a magnet's metadata is never fetched.</summary>
internal sealed class FakeTorrents : ITorrentMetadataService
{
    public List<string> Fetched { get; } = [];

    public TorrentMetadata Parse(byte[] torrentFile)
    {
        var text = Encoding.ASCII.GetString(torrentFile);
        return text.StartsWith("torrent:", StringComparison.Ordinal)
            ? new TorrentMetadata(text["torrent:".Length..], "Release", [new ListedFile("Release/01.mkv", 700)], torrentFile)
            : throw new InvalidDataException("Not a valid .torrent file.");
    }

    public Task<TorrentMetadata> FetchAsync(string magnet, CancellationToken cancellationToken)
    {
        Fetched.Add(magnet);
        return Task.FromResult(new TorrentMetadata(MagnetUri.GetInfoHash(magnet)!, "Swarm", [new ListedFile("Swarm/a.flac", 10)], [1, 2, 3]));
    }
}

/// <summary>The sources and providers of Convy's own API (<c>Convy.Sources.Abstractions</c>), loaded from the test package.</summary>
public class ContentSourceTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    private static FakeSiteHandler Indexers()
    {
        var web = new FakeSiteHandler();
        web.Json("http://indexers.test/api/indexers", """[ { "id": 1, "name": "Tracker one" }, { "id": 2, "name": "Tracker two" } ]""");
        return web;
    }

    private static async Task<IReadOnlyList<IContentSource>> AllAsync(ExtensionHost host)
    {
        var sources = new List<IContentSource>();
        foreach (var provider in host.Providers)
        {
            sources.AddRange(await provider.GetSourcesAsync(CancellationToken.None));
        }

        return sources;
    }

    [Fact]
    public async Task LoadsTheSourcesAndProvidersOfTheAppsOwnApi()
    {
        using var host = Extensions.ContentHost(Indexers());

        Assert.Empty(host.Problems);
        var extension = Assert.Single(host.Extensions);
        Assert.Equal("Convy.Tests.ContentExtension", extension.Package.Package);
        Assert.Equal(new Version(2, 0, 0, 0), extension.Package.HostApis["Convy.Sources.Abstractions"]);
        Assert.Equal(["test-books", "test-peers"], extension.Sources.Select(s => s.Id));
        Assert.Equal(["test-indexers"], extension.Providers.Select(p => p.Id));
        Assert.Empty(extension.Galleries);

        var sources = await AllAsync(host);
        Assert.Equal(["test-books", "test-peers", "test-indexers-1", "test-indexers-2"], sources.Select(s => s.Id));
        Assert.Equal([Protocol.Opds, Protocol.Soulseek, Protocol.Torrent, Protocol.Torrent], sources.Select(s => s.Protocol));
        Assert.Equal("Tracker two", sources[3].Name);
        Assert.All(sources, s => Assert.Equal(SourceStatus.Ok, s.Status));
        Assert.Same(sources[2], host.FindSource("TEST-INDEXERS-1"));
    }

    [Fact]
    public async Task ASourceTellsWhyItCannotBeUsedAndADisabledOneIsNotListed()
    {
        var options = new ExtensionsOptions();
        options.Settings["test-books"] = new ExtensionEntryOptions();
        options.Settings["test-indexers-2"] = new ExtensionEntryOptions { Enabled = false };
        options.Settings["test-peers"] = new ExtensionEntryOptions { Enabled = false };
        using var host = Extensions.ContentHost(Indexers(), options);

        var sources = await AllAsync(host);

        Assert.Equal(["test-books", "test-indexers-1"], sources.Select(s => s.Id));
        Assert.Equal((SourceStatus.Error, "No token set."), (sources[0].Status, sources[0].StatusMessage));
    }

    [Fact]
    public async Task AFailingProviderFailsItsOwnListingOnly()
    {
        var web = new FakeSiteHandler();
        web.Status("http://indexers.test/api/indexers", HttpStatusCode.Unauthorized);
        using var host = Extensions.ContentHost(web);

        var provider = Assert.Single(host.Providers, p => p.Name == "test-indexers");
        var failure = await Assert.ThrowsAsync<SourceException>(() => provider.GetSourcesAsync(CancellationToken.None));
        Assert.Equal((SourceErrorKind.AuthFailed, "The key was refused."), (failure.Kind, failure.Message));
        Assert.Equal(2, (await host.GetSourcesAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SearchesWithTheCategoriesOptionsForTheSourceOverItsProviders()
    {
        var web = Indexers();
        web.Json("http://indexers.test/api/1/search?q=fallen&cat=3010", $$"""[ { "link": "http://indexers.test/dl/7", "title": "Fallen", "seeders": 12, "hash": "{{Hash}}" } ]""");
        using var host = Extensions.ContentHost(web);
        var indexer = (await AllAsync(host)).Single(s => s.Id == "test-indexers-1");
        var category = new CategorySearchSettings("music", new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            ["test-indexers"] = new Dictionary<string, IReadOnlyList<string>> { ["categories"] = ["3000"] },
            ["test-indexers-1"] = new Dictionary<string, IReadOnlyList<string>> { ["categories"] = ["3010"] },
        });

        var found = Assert.Single(await indexer.SearchAsync(new SearchRequest("fallen", category), CancellationToken.None).ToListAsync());

        Assert.Equal(("http://indexers.test/dl/7", "Fallen", 12, $"btih:{Hash}"), (found.ContentId, found.Title, found.Availability.Seeders, found.DedupKey));
    }

    [Fact]
    public async Task ATorrentRedirectingToAMagnetIsThatMagnetFetchedWithTheSourcesKey()
    {
        var web = Indexers();
        var magnet = $"magnet:?xt=urn:btih:{Hash}&dn=Fallen";
        web.Answer("http://indexers.test/dl/7", _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://indexers.test/go/7") } });
        web.Answer("http://indexers.test/go/7", _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(magnet) } });
        var torrents = new FakeTorrents();
        using var host = Extensions.ContentHost(web, torrents: torrents);
        var indexer = (await AllAsync(host)).Single(s => s.Id == "test-indexers-1");

        var payload = Assert.IsType<TorrentPayload>(await indexer.ResolveAsync("http://indexers.test/dl/7", CancellationToken.None));
        Assert.Equal((Hash, magnet, (byte[]?)null), (payload.InfoHash, payload.Magnet, payload.TorrentFile));
        Assert.Equal("indexer-key", FakeSiteHandler.Header(web.Requests.Last(r => r.RequestUri!.AbsolutePath == "/go/7"), "X-Api-Key"));

        var listing = await indexer.ListFilesAsync("http://indexers.test/dl/7", CancellationToken.None);
        Assert.Equal(["Swarm/a.flac"], listing.Files.Select(f => f.Path));
        Assert.Equal([magnet], torrents.Fetched);
    }

    [Fact]
    public async Task ATorrentFileIsReadARedirectOffTheSourcesHostsGoesWithoutItsKeyAndAnAddressOffThemIsRefused()
    {
        var web = Indexers();
        web.Bytes("http://indexers.test/dl/8", Encoding.ASCII.GetBytes("torrent:" + Hash), "application/x-bittorrent");
        web.Answer("http://indexers.test/dl/9", _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://tracker.test/t") } });
        web.Bytes("http://tracker.test/t", Encoding.ASCII.GetBytes("torrent:" + Hash), "application/x-bittorrent");
        using var host = Extensions.ContentHost(web, torrents: new FakeTorrents());
        var indexer = (await AllAsync(host)).Single(s => s.Id == "test-indexers-1");

        var payload = Assert.IsType<TorrentPayload>(await indexer.ResolveAsync("http://indexers.test/dl/8", CancellationToken.None));
        Assert.Equal((Hash, (string?)null), (payload.InfoHash, payload.Magnet));
        Assert.Equal("torrent:" + Hash, Encoding.ASCII.GetString(payload.TorrentFile!));
        var listing = await indexer.ListFilesAsync("http://indexers.test/dl/8", CancellationToken.None);
        Assert.Equal(["Release/01.mkv"], listing.Files.Select(f => f.Path));
        Assert.NotNull(listing.TorrentFile);

        var redirected = Assert.IsType<TorrentPayload>(await indexer.ResolveAsync("http://indexers.test/dl/9", CancellationToken.None));
        Assert.Equal(Hash, redirected.InfoHash);
        Assert.Equal("indexer-key", FakeSiteHandler.Header(web.Requests.Last(r => r.RequestUri!.AbsolutePath == "/dl/9"), "X-Api-Key"));
        Assert.Null(FakeSiteHandler.Header(web.Requests.Single(r => r.RequestUri!.Host == "tracker.test"), "X-Api-Key"));

        var refused = await Assert.ThrowsAsync<SourceException>(() => indexer.ResolveAsync("http://elsewhere.test/t", CancellationToken.None));
        Assert.Contains("may not fetch http://elsewhere.test", refused.Message);
        Assert.DoesNotContain(web.Requested, u => u.Contains("elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASoulseekFolderIsListedRelativeToItsDirectory()
    {
        using var host = Extensions.ContentHost(new FakeSiteHandler());
        var peers = (await host.GetSourcesAsync(CancellationToken.None)).Single(s => s.Id == "test-peers");

        var found = Assert.Single(await peers.SearchAsync(new SearchRequest("album", new CategorySearchSettings("music")), CancellationToken.None).ToListAsync());
        Assert.Equal((true, 2, 1000L), (found.Availability.FreeUploadSlot, found.Availability.QueueLength, found.Availability.UploadSpeed));

        var listing = await peers.ListFilesAsync(found.ContentId, CancellationToken.None);
        Assert.Equal(["01.flac", "CD2/02.flac"], listing.Files.Select(f => f.Path));
        var payload = Assert.IsType<SoulseekPayload>(await peers.ResolveAsync(found.ContentId, CancellationToken.None));
        Assert.Equal(("peer", @"Music\Album", @"Music\Album\CD2\02.flac"), (payload.Username, payload.Directory, payload.Files[1].Filename));
    }

    [Fact]
    public async Task ABooksFormatsAreItsFilesAndItsPayloadNamesTheSource()
    {
        var web = new FakeSiteHandler();
        web.Json("https://books.test/search?q=%D0%BF%D0%B8%D0%BA%D0%BD%D0%B8%D0%BA", """[ { "id": "5", "title": "Пикник на обочине" } ]""");
        using var host = Extensions.ContentHost(web);
        var books = (await host.GetSourcesAsync(CancellationToken.None)).Single(s => s.Id == "test-books");
        var category = new CategorySearchSettings("books", new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            ["test-books"] = new Dictionary<string, IReadOnlyList<string>> { ["formats"] = ["epub", "fb2"] },
        });

        var found = Assert.Single(await books.SearchAsync(new SearchRequest("пикник", category), CancellationToken.None).ToListAsync());
        Assert.Equal(("5", "https://books.test/b/5", "epub, fb2"), (found.ContentId, found.InfoUrl, found.Properties["formats"]));

        var listing = await books.ListFilesAsync("5", CancellationToken.None);
        Assert.Equal([("Автор - Книга.epub", 0L), ("Автор - Книга.fb2", 2048L), ("Автор - Книга.mobi", 0L)], listing.Files.Select(f => (f.Path, f.Size)));
        var payload = Assert.IsType<OpdsPayload>(await books.ResolveAsync("5", CancellationToken.None));
        Assert.Equal(("test-books", "Автор - Книга", 24), (payload.CatalogId, payload.Folder, payload.BookKey.Length));
        Assert.Equal(new OpdsFile("Автор - Книга.fb2", "https://books.test/b/5/fb2", 2048, true, 1), payload.Files[1]);
    }

    [Theory]
    [InlineData("Автор - Книга.epub", "Автор - Книга.epub")]
    [InlineData("../../../data/convy.db", ".._.._.._data_convy.db")]
    [InlineData("/config", "_config")]
    [InlineData("..", "_..")]
    [InlineData(".", "_.")]
    [InlineData("", "_")]
    [InlineData("AC/DC\\Back in Black", "AC_DC_Back in Black")]
    public void ANameAReleaseGivesIsOneFileNameUnderItsFolder(string name, string segment) =>
        Assert.Equal(segment, PackagedSource.Segment(name));
}
