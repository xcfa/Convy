using System.Text.Json;
using Convy.Sources.Abstractions;
using Shizuka.Sources.Abstractions;

namespace Convy.Tests.ContentExtension;

/// <summary>
/// A book catalog at <c>https://books.test</c>: <c>/search?q=</c> answers JSON books, whose formats Convy fetches with
/// the catalog's token. A content id is a book's id.
/// </summary>
public sealed class TestBooks : ContentSource
{
    public const string Site = "https://books.test";

    public override SourceManifest Manifest { get; } = new()
    {
        Id = "test-books",
        Name = "Test books",
        Domains = ["books.test"],
        Settings = [new SourceSettingDefinition { Key = "token", Label = "Token", Kind = SourceSettingKind.Secret }],
    };

    public override ContentProtocol Protocol => ContentProtocol.Files;

    public override async Task<string?> CheckAsync(ISourceContext context, CancellationToken ct) =>
        context.Settings.Get("token") is null ? "No token set." : null;

    public override async Task<IReadOnlyList<ContentItem>> SearchAsync(ContentQuery query, ISourceContext context, CancellationToken ct)
    {
        var response = await context.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{Site}/search?q={Uri.EscapeDataString(query.Text)}"), ct);
        if (!response.IsSuccess)
            throw new SourceException($"The catalog answered {response.StatusCode}.");
        using var json = JsonDocument.Parse(response.Body);
        return json.RootElement.EnumerateArray().Select(b => new ContentItem
        {
            ContentId = b.GetProperty("id").GetString()!,
            Title = b.GetProperty("title").GetString()!,
            InfoUrl = new Uri($"{Site}/b/{b.GetProperty("id").GetString()}"),
            Properties = new Dictionary<string, string> { ["language"] = "ru", ["formats"] = string.Join(", ", query.Option("formats")) },
        }).ToList();
    }

    public override Task<ContentRelease> ResolveAsync(string contentId, ISourceContext context, CancellationToken ct) =>
        Task.FromResult<ContentRelease>(new FilesRelease("Автор - Книга",
        [
            new FileLink("Автор - Книга.epub", new Uri($"{Site}/b/{contentId}/epub"), Preference: 0),
            new FileLink("Автор - Книга.fb2", new Uri($"{Site}/b/{contentId}/fb2"), 2048, Unzip: true, Preference: 1),
            new FileLink("Автор - Книга.mobi", new Uri($"{Site}/b/{contentId}/mobi")),
        ]));

    public override IReadOnlyDictionary<string, string>? FetchHeaders(ISourceContext context) =>
        context.Settings.Get("token") is { } token ? new Dictionary<string, string> { ["X-Token"] = token } : null;
}

/// <summary>
/// An indexer service at the address of its <c>url</c> setting: <c>/api/indexers</c> lists its indexers, each a torrent
/// source <c>test-indexers-&lt;id&gt;</c>; <c>/api/{id}/search?q=</c> finds releases.
/// </summary>
public sealed class TestIndexers : ContentSourceProvider
{
    public override SourceManifest Manifest { get; } = new()
    {
        Id = "test-indexers",
        Name = "Test indexers",
        Domains = [],
        Settings =
        [
            new SourceSettingDefinition { Key = "url", Label = "Address", Kind = SourceSettingKind.Url },
            new SourceSettingDefinition { Key = "key", Label = "Key", Kind = SourceSettingKind.Secret },
        ],
    };

    public override async Task<IReadOnlyList<ContentSource>> GetSourcesAsync(ISourceContext context, CancellationToken ct)
    {
        var url = context.Settings.Get("url") ?? throw new SourceException("No address set.");
        var response = await context.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/api/indexers"), ct);
        if (response.StatusCode == 401)
            throw new SourceException("The key was refused.") { Kind = SourceErrorKind.AuthFailed };
        using var json = JsonDocument.Parse(response.Body);
        return json.RootElement.EnumerateArray()
            .Select(i => (ContentSource)new TestIndexer(i.GetProperty("id").GetInt32(), i.GetProperty("name").GetString()!, url))
            .ToList();
    }
}

/// <summary>One indexer of <see cref="TestIndexers"/>: a release's content id is the address of its .torrent.</summary>
public sealed class TestIndexer(int id, string name, string url) : ContentSource
{
    public override SourceManifest Manifest { get; } = new()
    {
        Id = $"test-indexers-{id}",
        Name = name,
        Domains = [],
    };

    public override ContentProtocol Protocol => ContentProtocol.Torrent;

    public override async Task<IReadOnlyList<ContentItem>> SearchAsync(ContentQuery query, ISourceContext context, CancellationToken ct)
    {
        var categories = string.Join(",", query.Option("categories"));
        var response = await context.Http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/api/{id}/search?q={Uri.EscapeDataString(query.Text)}&cat={categories}"), ct);
        using var json = JsonDocument.Parse(response.Body);
        return json.RootElement.EnumerateArray().Select(r => new ContentItem
        {
            ContentId = r.GetProperty("link").GetString()!,
            Title = r.GetProperty("title").GetString()!,
            Seeders = r.GetProperty("seeders").GetInt32(),
            DedupKey = r.TryGetProperty("hash", out var hash) ? $"btih:{hash.GetString()}" : null,
        }).ToList();
    }

    public override Task<ContentRelease> ResolveAsync(string contentId, ISourceContext context, CancellationToken ct) =>
        Task.FromResult<ContentRelease>(new TorrentRelease { TorrentUrl = new Uri(contentId) });

    public override IReadOnlyDictionary<string, string>? FetchHeaders(ISourceContext context) =>
        context.Settings.Get("key") is { } key ? new Dictionary<string, string> { ["X-Api-Key"] = key } : null;
}

/// <summary>A Soulseek-like source: a content id is <c>user|dir</c>; the folder has two files.</summary>
public sealed class TestPeers : ContentSource
{
    public override SourceManifest Manifest { get; } = new()
    {
        Id = "test-peers",
        Name = "Test peers",
        Domains = [],
        Settings = [new SourceSettingDefinition { Key = "url", Label = "Address", Kind = SourceSettingKind.Url }],
    };

    public override ContentProtocol Protocol => ContentProtocol.Soulseek;

    public override Task<IReadOnlyList<ContentItem>> SearchAsync(ContentQuery query, ISourceContext context, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ContentItem>>(
        [
            new ContentItem { ContentId = @"peer|Music\Album", Title = "Album — peer", FreeUploadSlot = true, QueueLength = 2, UploadSpeed = 1000 },
        ]);

    public override Task<ContentRelease> ResolveAsync(string contentId, ISourceContext context, CancellationToken ct)
    {
        var (user, directory) = (contentId.Split('|')[0], contentId.Split('|')[1]);
        return Task.FromResult<ContentRelease>(new SoulseekRelease(user, directory,
            [new ContentFile(directory + @"\01.flac", 100), new ContentFile(directory + @"\CD2\02.flac", 200)]));
    }
}
