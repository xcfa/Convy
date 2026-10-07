using Convy.Services.Media;
using Convy.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convy.Services.Tests;

public sealed class SearchServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTime _time = new();
    private readonly SearchOptions _options = new() { BatchSize = 2, MaxQueryVariants = 3, SourceTimeoutSeconds = 1, MaxResults = 30 };
    private readonly FakeSource _a = new("prowlarr:1", "Alpha");
    private readonly FakeSource _b = new("prowlarr:2", "Bravo");
    private readonly FakeSource _c = new("prowlarr:3", "Charlie");
    private readonly EfSearchCache _cache;

    public SearchServiceTests() => _cache = new EfSearchCache(_db, _time);

    public void Dispose() => _db.Dispose();

    private SearchService Create(params string[] musicSources) => new(
        Media.Catalog(Media.Categories(musicSources)),
        new SourceRegistry([new StaticProvider(_a, _b, _c)], _time, NullLogger<SourceRegistry>.Instance),
        _cache,
        new StaticOptions<SearchOptions>(_options),
        _time,
        NullLogger<SearchService>.Instance);

    [Fact]
    public async Task SearchesTheFirstBatchInCategoryOrderWithEveryVariant()
    {
        _c.Results["fallen"] = [FakeSource.Content("Fallen (C)")];
        var service = Create("prowlarr:3", "prowlarr:1", "prowlarr:2");

        var response = await service.StartAsync("music", ["fallen", "Fallen ", "evanescence fallen"], null, CancellationToken.None);

        Assert.StartsWith("s_", response.SearchId);
        Assert.Equal(["prowlarr:3", "prowlarr:1"], response.Sources.Select(s => s.Id));
        Assert.Equal(["evanescence fallen", "fallen"], _c.Queries.Order(StringComparer.Ordinal)); // trimmed, de-duplicated
        Assert.Empty(_b.Queries);
        Assert.True(response.HasMore);
        Assert.Equal("Fallen (C)", Assert.Single(response.Results).Title);
    }

    [Fact]
    public async Task ReportsAStatusPerSource()
    {
        _a.Results["q"] = [FakeSource.Content("x")];
        _b.Failure = new SourceException(SourceErrorKind.AuthFailed, "cookies expired");
        _c.Hang = true;
        _options.BatchSize = 3;
        var empty = new FakeSource("prowlarr:4", "Delta");
        var service = new SearchService(
            Media.Catalog(Media.Categories("prowlarr:1", "prowlarr:2", "prowlarr:3", "prowlarr:4")),
            new SourceRegistry([new StaticProvider(_a, _b, _c, empty)], _time, NullLogger<SourceRegistry>.Instance),
            _cache, new StaticOptions<SearchOptions>(_options), _time, NullLogger<SearchService>.Instance);

        var first = await service.StartAsync("music", ["q"], null, CancellationToken.None);
        var next = await service.NextAsync(first.SearchId, CancellationToken.None);

        Assert.Equal(["ok", "auth_failed", "timeout"], first.Sources.Select(s => s.Status));
        Assert.Equal("cookies expired", first.Sources[1].Message);
        Assert.Equal("empty", Assert.Single(next.Sources).Status);
        Assert.False(next.HasMore);
    }

    [Fact]
    public async Task MergesTheSameTorrentAcrossSourcesAndVariants()
    {
        _a.Results["fallen"] = [FakeSource.Content("Fallen [FLAC]", hash: "aa", seeders: 5, contentId: "a1")];
        _a.Results["evanescence fallen"] = [FakeSource.Content("Fallen [FLAC]", hash: "aa", seeders: 5, contentId: "a1")];
        _b.Results["fallen"] = [FakeSource.Content("Evanescence - Fallen", hash: "aa", seeders: 9, contentId: "b1")];

        var response = await Create("prowlarr:1", "prowlarr:2").StartAsync("music", ["fallen", "evanescence fallen"], null, CancellationToken.None);

        var result = Assert.Single(response.Results);
        Assert.Equal("Fallen [FLAC]", result.Title);                          // highest-priority source wins
        Assert.Equal(["prowlarr:1", "prowlarr:2"], result.Sources);
        Assert.Equal(["fallen", "evanescence fallen"], result.MatchedQueries);
        Assert.Equal(9, result.Availability.Seeders);                         // best availability
    }

    [Fact]
    public async Task SortsBySourcePriorityThenAvailability()
    {
        _a.Results["q"] = [FakeSource.Content("A few", "1", seeders: 1), FakeSource.Content("A many", "2", seeders: 50)];
        _b.Results["q"] = [FakeSource.Content("B most", "3", seeders: 500)];

        var response = await Create("prowlarr:1", "prowlarr:2").StartAsync("music", ["q"], null, CancellationToken.None);

        Assert.Equal(["A many", "A few", "B most"], response.Results.Select(r => r.Title));
    }

    [Fact]
    public async Task NextStepDoesNotRepeatShownResults()
    {
        _a.Results["q"] = [FakeSource.Content("Shared", "aa")];
        _c.Results["q"] = [FakeSource.Content("Shared elsewhere", "aa"), FakeSource.Content("Only C", "cc")];
        var service = Create("prowlarr:1", "prowlarr:2", "prowlarr:3");

        var first = await service.StartAsync("music", ["q"], null, CancellationToken.None);
        var next = await service.NextAsync(first.SearchId, CancellationToken.None);

        Assert.Equal("Only C", Assert.Single(next.Results).Title);
        var shared = await _cache.GetResultAsync(first.Results[0].Id, CancellationToken.None);
        Assert.Equal(["prowlarr:1", "prowlarr:3"], shared!.Sources);
    }

    [Fact]
    public async Task TruncatesToMaxResultsWithANote()
    {
        _options.MaxResults = 2;
        _a.Results["q"] = Enumerable.Range(0, 5).Select(i => FakeSource.Content($"r{i}", $"h{i}", seeders: i)).ToList();

        var response = await Create("prowlarr:1").StartAsync("music", ["q"], null, CancellationToken.None);

        Assert.Equal(2, response.Shown);
        Assert.Equal(5, response.Total);
        Assert.Contains("2 of 5", response.Note);
    }

    [Fact]
    public async Task ExplicitSourcesReplaceTheCategoryListAndUnknownOnesAreReported()
    {
        _b.Results["q"] = [FakeSource.Content("from b")];

        var response = await Create("prowlarr:1").StartAsync("music", ["q"], ["prowlarr:2", "prowlarr:99"], CancellationToken.None);

        Assert.Equal(["prowlarr:2", "prowlarr:99"], response.Sources.Select(s => s.Id));
        Assert.Equal("error", response.Sources[1].Status);
        Assert.Empty(_a.Queries);
    }

    [Fact]
    public async Task WithoutCategorySourcesAllActiveSourcesAreSearchedByName()
    {
        _options.BatchSize = 10;

        var response = await Create().StartAsync("music", ["q"], null, CancellationToken.None);

        Assert.Equal(["prowlarr:1", "prowlarr:2", "prowlarr:3"], response.Sources.Select(s => s.Id));
    }

    [Fact]
    public async Task UnknownCategoryListsTheAvailableOnes()
    {
        var ex = await Assert.ThrowsAsync<ConvyRequestException>(() => Create().StartAsync("films", ["q"], null, CancellationToken.None));
        Assert.Contains("music, other", ex.Message);
    }

    [Fact]
    public async Task EmptyQueriesAreRejected()
    {
        await Assert.ThrowsAsync<ConvyRequestException>(() => Create().StartAsync("music", [" ", ""], null, CancellationToken.None));
    }

    [Fact]
    public async Task ExtraQueryVariantsAreDropped()
    {
        _options.MaxQueryVariants = 2;

        await Create("prowlarr:1").StartAsync("music", ["a", "b", "c"], null, CancellationToken.None);

        Assert.Equal(["a", "b"], _a.Queries.Order());
    }

    [Fact]
    public async Task ExpiredSearchCannotContinue()
    {
        var service = Create("prowlarr:1", "prowlarr:2", "prowlarr:3");
        var first = await service.StartAsync("music", ["q"], null, CancellationToken.None);

        _time.Now += TimeSpan.FromHours(7);

        await Assert.ThrowsAsync<ConvyRequestException>(() => service.NextAsync(first.SearchId, CancellationToken.None));
    }
}
