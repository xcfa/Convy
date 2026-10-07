using System.Net;
using System.Text;
using Convy.Sources;
using Convy.Sources.Prowlarr;
using Convy.Sources.Torrents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public class ProwlarrTests
{
    private sealed class FakeProwlarr : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class FakeMetadata : ITorrentMetadataService
    {
        private readonly TorrentMetadataService _parser = new(new TorrentMetadataOptions(), NullLogger<TorrentMetadataService>.Instance);
        public List<string> Fetched { get; } = [];

        public TorrentMetadata Parse(byte[] torrentFile) => _parser.Parse(torrentFile);

        public Task<TorrentMetadata> FetchAsync(string magnet, CancellationToken cancellationToken)
        {
            Fetched.Add(magnet);
            return Task.FromResult(_parser.Parse(TorrentMetadataTests.Torrent("FromDht", ("a.flac", 7), ("b.flac", 8))));
        }
    }

    private static readonly ProwlarrOptions Options = new() { Url = "http://prowlarr:9696", ApiKey = "secret" };
    private static readonly CategorySearchSettings Movies = new("movies", [2000, 5000], []);

    private static (ProwlarrClient Client, FakeProwlarr Http) Client()
    {
        var http = new FakeProwlarr();
        return (new ProwlarrClient(new HttpClient(http) { BaseAddress = new Uri("http://prowlarr:9696/") }, Options), http);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ProwlarrSource Source(ProwlarrClient client, ITorrentMetadataService? metadata = null) =>
        new(12, "RuTracker", SourceStatus.Ok, null, client, metadata ?? new FakeMetadata(), NullLogger<ProwlarrSource>.Instance);

    private static async Task<List<ContentInfo>> Search(ProwlarrSource source, string query = "fallen")
    {
        var list = new List<ContentInfo>();
        await foreach (var c in source.SearchAsync(new SearchRequest(query, Movies), CancellationToken.None))
            list.Add(c);
        return list;
    }

    [Fact]
    public async Task SearchSendsOneIndexerRepeatedCategoriesAndTheKeyHeader()
    {
        var (client, http) = Client();
        http.Respond = _ => Json("""
            [{"guid":"g1","title":"Evanescence - Fallen","size":1000,"files":12,"indexerId":12,
              "downloadUrl":"http://prowlarr:9696/12/download?apikey=secret&link=x","infoHash":"0123456789ABCDEF0123456789ABCDEF01234567",
              "seeders":42,"leechers":3,"protocol":"torrent"},
             {"guid":"g2","title":"Some NZB","size":5,"indexerId":12,"protocol":"usenet"}]
            """);

        var results = await Search(Source(client), "evanescence fallen");

        var request = Assert.Single(http.Requests);
        Assert.Equal(
            "/api/v1/search?query=evanescence%20fallen&indexerIds=12&type=search&limit=100&categories=2000&categories=5000",
            request.RequestUri!.PathAndQuery);
        Assert.Equal("secret", request.Headers.GetValues("X-Api-Key").Single());

        var result = Assert.Single(results);
        Assert.Equal("Evanescence - Fallen", result.Title);
        Assert.Equal("btih:0123456789abcdef0123456789abcdef01234567", result.DedupKey);
        Assert.Equal(42, result.Availability.Seeders);
        Assert.Equal(12, result.FileCount);
    }

    [Fact]
    public async Task EmptyResultFromAFailingIndexerIsAnError()
    {
        var (client, http) = Client();
        http.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("indexerstatus")
            ? Json("""[{"indexerId":12,"disabledTill":"2026-10-07T12:35:00Z","mostRecentFailure":"2026-10-07T12:34:00Z"}]""")
            : Json("[]");

        var ex = await Assert.ThrowsAsync<SourceException>(() => Search(Source(client)));
        Assert.Equal(SourceErrorKind.Error, ex.Kind);
    }

    [Fact]
    public async Task EmptyResultFromAHealthyIndexerIsEmpty()
    {
        var (client, http) = Client();
        http.Respond = _ => Json("[]");

        Assert.Empty(await Search(Source(client)));
    }

    [Fact]
    public async Task RejectedKeyIsAnAuthFailure()
    {
        var (client, http) = Client();
        http.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<SourceException>(() => Search(Source(client)));
        Assert.Equal(SourceErrorKind.AuthFailed, ex.Kind);
    }

    [Fact]
    public async Task AllIndexersUnavailableMessageIsReported()
    {
        var (client, http) = Client();
        http.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"Search failed due to all selected indexers being unavailable"}"""),
        };

        var ex = await Assert.ThrowsAsync<SourceException>(() => Search(Source(client)));
        Assert.Contains("all selected indexers being unavailable", ex.Message);
    }

    [Fact]
    public async Task TorrentFileLinkIsParsedForListingAndResolving()
    {
        var torrent = TorrentMetadataTests.Torrent("Show", ("e1.mkv", 10), ("e2.mkv", 20));
        var (client, http) = Client();
        http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(torrent) };
        var source = Source(client);
        var contentId = ProwlarrContentRef.Serialize(new("g", "Show", "http://prowlarr:9696/12/download?apikey=secret&link=x", null, null));

        var listing = await source.ListFilesAsync(contentId, CancellationToken.None);
        var payload = Assert.IsType<TorrentPayload>(await source.ResolveAsync(contentId, CancellationToken.None));

        Assert.Equal(["e1.mkv", "e2.mkv"], listing.Files.Select(f => f.Path));
        Assert.Equal(torrent, payload.TorrentFile);
        Assert.Null(payload.Magnet);
        Assert.Equal(40, payload.InfoHash.Length);
        // Download links carry their own key; no header is added to them.
        Assert.All(http.Requests, r => Assert.False(r.Headers.Contains("X-Api-Key")));
    }

    [Fact]
    public async Task MagnetRedirectIsFetchedFromTheSwarmAndResolvedToAMagnet()
    {
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=FromDht";
        var (client, http) = Client();
        http.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri(magnet);
            return response;
        };
        var metadata = new FakeMetadata();
        var source = Source(client, metadata);
        var contentId = ProwlarrContentRef.Serialize(new("g", "x", null, "http://prowlarr:9696/12/download?apikey=secret&link=m", null));

        var listing = await source.ListFilesAsync(contentId, CancellationToken.None);
        var payload = Assert.IsType<TorrentPayload>(await source.ResolveAsync(contentId, CancellationToken.None));

        Assert.Equal(magnet, Assert.Single(metadata.Fetched));
        Assert.Equal(["a.flac", "b.flac"], listing.Files.Select(f => f.Path));
        Assert.NotNull(listing.TorrentFile);
        Assert.Equal(magnet, payload.Magnet);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", payload.InfoHash);
    }

    [Fact]
    public async Task ProviderOffersEnabledTorrentIndexersWithStatus()
    {
        var (client, http) = Client();
        http.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/v1/indexer" => Json("""
                [{"id":12,"name":"RuTracker","enable":true,"protocol":"torrent","priority":25},
                 {"id":20,"name":"Blocked","enable":true,"protocol":"torrent","priority":25},
                 {"id":7,"name":"Off","enable":false,"protocol":"torrent","priority":25},
                 {"id":9,"name":"Nzb","enable":true,"protocol":"usenet","priority":25}]
                """),
            _ => Json("""[{"indexerId":20,"disabledTill":"2026-10-07T12:35:00Z"}]"""),
        };
        var provider = new ProwlarrSourceProvider(client, Options, new FakeMetadata(), NullLogger<ProwlarrSource>.Instance);

        var sources = await provider.GetSourcesAsync(CancellationToken.None);

        Assert.Equal(["prowlarr:12", "prowlarr:20", "prowlarr:7"], sources.Select(s => s.Id));
        Assert.Equal([SourceStatus.Ok, SourceStatus.Error, SourceStatus.Disabled], sources.Select(s => s.Status));
    }

    [Fact]
    public async Task UnconfiguredProwlarrOffersNothing()
    {
        var (client, http) = Client();
        var provider = new ProwlarrSourceProvider(client, new ProwlarrOptions(), new FakeMetadata(), NullLogger<ProwlarrSource>.Instance);

        Assert.Empty(await provider.GetSourcesAsync(CancellationToken.None));
        Assert.Empty(http.Requests);
    }
}
