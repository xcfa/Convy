using System.Runtime.CompilerServices;
using System.Text.Json;
using Convy.Sources;

namespace Convy.Sources.TestPlugin;

/// <summary>A gallery site at <c>https://gallery.test</c> with a JSON API, as the tests fake it.</summary>
public sealed class TestPlugin : SourcePlugin
{
    public override PluginManifest Manifest { get; } = new()
    {
        Id = "test-gallery",
        Name = "Test gallery",
        Domains = ["gallery.test", "img.gallery.test"],
        Settings = [new PluginSetting("token", "Token", "An access token; a source without it reports an error.", Secret: true)],
        RequestsPerSecond = 50,
    };

    public override IReadOnlyList<IContentSource> CreateSources(IPluginContext context) => [new TestGallerySource(context)];
}

public sealed class TestGallerySource(IPluginContext context) : IGallerySource, IGalleryBrowser
{
    public const string Site = "https://gallery.test";

    public string Id => "test-gallery";

    public string Name => "Test gallery";

    public Protocol Protocol => Protocol.Gallery;

    public SourceStatus Status => context.Settings.Get("token") is null ? SourceStatus.Error : SourceStatus.Ok;

    public string? StatusMessage => context.Settings.Get("token") is null ? "Set the token." : null;

    public GalleryManifest Manifest { get; } = new()
    {
        Domains = ["gallery.test"],
        Nsfw = true,
        Languages = ["ja", "en"],
        DefaultAgeRating = 18,
        Filters = [new GalleryFilter("category", "Category", [new GalleryFilterOption("manga", "Manga"), new GalleryFilterOption("doujinshi", "Doujinshi")])],
    };

    public async IAsyncEnumerable<ContentInfo> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = await BrowseAsync(request.Query, request.Gallery ?? GallerySearch.Empty, cancellationToken);
        foreach (var item in page.Items)
        {
            yield return item;
        }
    }

    public async Task<GallerySearchResult> BrowseAsync(string? query, GallerySearch search, CancellationToken cancellationToken)
    {
        var url = $"{Site}/api/search?q={Uri.EscapeDataString(query ?? "")}&page={search.Page ?? "1"}"
                  + (search.Language is null ? "" : $"&lang={search.Language}")
                  + string.Concat(search.Filters.SelectMany(f => f.Value.Select(v => $"&{f.Key}={Uri.EscapeDataString(v)}")));
        using var json = await GetJsonAsync(url, cancellationToken);
        var items = json.RootElement.GetProperty("items").EnumerateArray().Select(Content).ToList();
        var next = json.RootElement.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return new GallerySearchResult(items, next);
    }

    public Task<FileListing> ListFilesAsync(string contentId, CancellationToken cancellationToken) =>
        Task.FromResult(new FileListing([]));

    public async Task<DownloadPayload> ResolveAsync(string contentId, CancellationToken cancellationToken)
    {
        var info = await GetAsync(contentId, cancellationToken) ?? throw new SourceException(SourceErrorKind.Error, $"No gallery {contentId}.");
        return new GalleryPayload(Id, contentId, info.Title, info.FileCount, info.InfoUrl);
    }

    public async Task<ContentInfo?> ResolveUrlAsync(Uri url, CancellationToken cancellationToken)
    {
        if (url.Host != "gallery.test" || !url.AbsolutePath.StartsWith("/g/", StringComparison.Ordinal))
        {
            return null;
        }

        return await GetAsync(url.AbsolutePath[3..].Trim('/'), cancellationToken);
    }

    public async Task<IReadOnlyList<GalleryPage>> GetPagesAsync(string contentId, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"{Site}/api/g/{contentId}/pages", cancellationToken);
        return json.RootElement.EnumerateArray()
            .Select((p, i) => new GalleryPage(i + 1, new Uri($"{Site}/g/{contentId}/{i + 1}"), p.TryGetProperty("image", out var img) ? new Uri(img.GetString()!) : null))
            .ToList();
    }

    public async Task<Uri> GetImageUrlAsync(GalleryPage page, CancellationToken cancellationToken)
    {
        using var json = await GetJsonAsync($"{Site}/api{page.Url.AbsolutePath}", cancellationToken);
        return new Uri(json.RootElement.GetProperty("image").GetString()!);
    }

    public Task<Uri?> RetryImageUrlAsync(GalleryPage page, Uri failed, CancellationToken cancellationToken) =>
        Task.FromResult<Uri?>(failed.AbsolutePath.Contains("/broken/", StringComparison.Ordinal)
            ? new Uri(failed.AbsoluteUri.Replace("/broken/", "/img/", StringComparison.Ordinal))
            : null);

    public async Task<GalleryImage> OpenImageAsync(Uri url, CancellationToken cancellationToken)
    {
        var response = await context.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), streaming: true, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new SourceException(SourceErrorKind.Error, $"The site answered {(int)response.StatusCode} for {url}.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new GalleryImage(new Owned(stream, response), response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentLength);
    }

    private async Task<ContentInfo?> GetAsync(string contentId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Site}/api/g/{contentId}");
        request.Headers.TryAddWithoutValidation("X-Token", context.Settings.Get("token") ?? "");
        using var response = await context.Http.SendAsync(request, streaming: false, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.Headers.TryGetValues("X-Captcha", out var captcha))
        {
            // The site's own captcha (not a bot check the host sees): the plugin reports where a person passes it.
            throw new SourceException(SourceErrorKind.Challenge, "The site asks for its captcha.") { ChallengeUrl = new Uri(captcha.First()) };
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new SourceException(SourceErrorKind.AuthFailed, "The token was refused.");
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return Content(json.RootElement);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await context.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), streaming: false, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SourceException(SourceErrorKind.Error, $"The site answered {(int)response.StatusCode}.");
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static ContentInfo Content(JsonElement g)
    {
        var id = g.GetProperty("id").GetString()!;
        var properties = new Dictionary<string, string>();
        foreach (var property in g.EnumerateObject().Where(p => p.Name is not ("id" or "title" or "pages")))
        {
            properties[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                ? string.Join(", ", property.Value.EnumerateArray().Select(v => v.GetString()))
                : property.Value.ToString();
        }

        properties["pages"] = g.GetProperty("pages").GetInt32().ToString();
        return new ContentInfo
        {
            ContentId = id,
            Title = g.GetProperty("title").GetString()!,
            FileCount = g.GetProperty("pages").GetInt32(),
            InfoUrl = $"{Site}/g/{id}",
            DedupKey = $"test:{id}",
            Properties = properties,
        };
    }

    /// <summary>A stream that disposes the response with it.</summary>
    private sealed class Owned(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
