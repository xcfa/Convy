using System.Globalization;
using System.Runtime.CompilerServices;
using Convy.Sources;
using Microsoft.Extensions.Logging;
using Shizuka.Sources.Hosting;
using Api = Shizuka.Sources.Abstractions;

namespace Convy.Services.Extensions;

/// <summary>
/// One source of a Shizuka extension as Convy's gallery source. A release is a link the source resolves and downloads
/// (its address is the content id); a series is not one, its chapters are. A search with text finds releases; when the
/// site's catalog lists series, the best match's chapters are the answer, a page of them at a time. Each call gets a
/// context like Shizuka's: HTTP on the manifest's domains with the source's cookies, rate gate, proxy and timeout, the
/// settings of <c>extensions.settings.&lt;id&gt;.values</c> checked by their kind, and the service of checks
/// (<c>captcha</c>) in the source's own profile there.
/// </summary>
public sealed class ExtensionSource : ExtensionUnit, IGallerySource, IGalleryBrowser
{
    /// <summary>Chapters of a series go to a search this many at a time.</summary>
    public const int ChaptersPerPage = 100;

    /// <summary>The largest image a page may have.</summary>
    private const long MaxImageBytes = 64L * 1024 * 1024;

    private readonly Api.MetadataSource _source;

    public ExtensionSource(Api.MetadataSource source, string package, ExtensionHost host, ILogger logger)
        : base(source.Manifest, package, host, logger)
    {
        _source = source;
        var manifest = source.Manifest;
        Manifest = new GalleryManifest
        {
            Domains = manifest.Domains,
            Nsfw = manifest.Nsfw,
            Languages = manifest.Languages,
            DefaultAgeRating = manifest.DefaultAgeRating is { } rating ? (int)rating : null,
            CanSearch = manifest.Capabilities.HasFlag(Api.SourceCapabilities.Browse) || manifest.Capabilities.HasFlag(Api.SourceCapabilities.Search),
            Filters = manifest.BrowseFilters
                .Select(f => new GalleryFilter(f.Key, f.Label, f.Options.Select(o => new GalleryFilterOption(o.Value, o.Label)).ToList()))
                .ToList(),
        };
    }

    public string Name => _source.Manifest.Name;

    public Protocol Protocol => Protocol.Gallery;

    public SourceStatus Status => SourceStatus.Ok;

    public string? StatusMessage => null;

    public GalleryManifest Manifest { get; }

    public async IAsyncEnumerable<ContentInfo> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var page = await BrowseAsync(request.Query, request.Gallery ?? GallerySearch.Empty, cancellationToken).ConfigureAwait(false);
        foreach (var item in page.Items)
        {
            yield return item;
        }
    }

    /// <summary>
    /// A page of releases: the chapters of a series link, the release of a link, or what the text finds (the best
    /// matching series' chapters where the catalog lists series). Without text, the site's front page, its series left
    /// out. Page tokens are <c>b:</c> + the site's own, or <c>c:</c> + the offset and the series' address.
    /// </summary>
    public Task<GallerySearchResult> BrowseAsync(string? query, GallerySearch search, CancellationToken cancellationToken) => CallAsync(async (context, ct) =>
    {
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var capabilities = _source.Manifest.Capabilities;
        if (search.Page is { } token && token.StartsWith("c:", StringComparison.Ordinal) && token[2..].Split(':', 2) is [var at, var listed]
            && int.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && Uri.TryCreate(listed, UriKind.Absolute, out var seriesUrl))
        {
            return await ChaptersAsync(seriesUrl, offset, search.Language, context, ct).ConfigureAwait(false);
        }

        if (text is not null && Uri.TryCreate(text, UriKind.Absolute, out var link) && link.Scheme is "http" or "https" && _source.CanResolve(link))
        {
            var metadata = await _source.ResolveAsync(link, context, ct).ConfigureAwait(false);
            if (metadata is null)
            {
                return new GallerySearchResult([], null);
            }

            return IsSeries(metadata)
                ? await ChaptersAsync(metadata.Url, 0, search.Language, context, ct).ConfigureAwait(false)
                : new GallerySearchResult([Content(metadata)], null);
        }

        IReadOnlyList<Api.SourceCandidate> found;
        string? next = null;
        if (capabilities.HasFlag(Api.SourceCapabilities.Browse))
        {
            var page = await _source.BrowseAsync(new Api.SourceBrowseQuery
            {
                Text = text,
                Page = search.Page is { } b && b.StartsWith("b:", StringComparison.Ordinal) ? b[2..] : null,
                Language = search.Language,
                Filters = search.Filters,
            }, context, ct).ConfigureAwait(false);
            found = page.Items ?? [];
            next = page.NextPage is { } more ? "b:" + more : null;
        }
        else if (text is not null && capabilities.HasFlag(Api.SourceCapabilities.Search))
        {
            found = await _source.SearchAsync(new Api.SourceQuery { Titles = [text], Language = search.Language }, context, ct).ConfigureAwait(false) ?? [];
        }
        else
        {
            return new GallerySearchResult([], null);
        }

        var series = found.Where(c => c is { IsSeries: true }).ToList();
        if (text is not null && series.Count > 0 && capabilities.HasFlag(Api.SourceCapabilities.Chapters))
        {
            var best = series.FirstOrDefault(s => string.Equals(s.Title.Trim(), text, StringComparison.OrdinalIgnoreCase)) ?? series[0];
            return await ChaptersAsync(best.Url, 0, search.Language, context, ct).ConfigureAwait(false);
        }

        return new GallerySearchResult(found.Where(c => c is { IsSeries: false, Url: not null }).Select(Content).ToList(), next);
    }, cancellationToken);

    /// <summary>The release at a link; a series or a page that is no release — <c>null</c>.</summary>
    public Task<ContentInfo?> ResolveUrlAsync(Uri url, CancellationToken cancellationToken)
    {
        if (!_source.CanResolve(url))
        {
            return Task.FromResult<ContentInfo?>(null);
        }

        return CallAsync<ContentInfo?>(async (context, ct) =>
        {
            var metadata = await _source.ResolveAsync(url, context, ct).ConfigureAwait(false);
            if (metadata is null || IsSeries(metadata))
            {
                return null;
            }

            return Content(metadata);
        }, cancellationToken);
    }

    public Task<FileListing> ListFilesAsync(string contentId, CancellationToken cancellationToken) => CallAsync(async (context, ct) =>
    {
        var pages = await PagesAsync(contentId, context, ct).ConfigureAwait(false);
        return new FileListing(pages.Select((p, i) => new ListedFile(PageName(p.Url, i + 1), 0)).ToList());
    }, cancellationToken);

    public Task<DownloadPayload> ResolveAsync(string contentId, CancellationToken cancellationToken) => CallAsync<DownloadPayload>(async (context, ct) =>
    {
        var url = Address(contentId);
        var metadata = await _source.ResolveAsync(url, context, ct).ConfigureAwait(false);
        if (metadata is null || IsSeries(metadata))
        {
            throw new Api.SourceException($"There is no release at {url}.");
        }

        return new GalleryPayload(Id, contentId, ReleaseTitle(metadata), metadata.PageCount, metadata.Url.AbsoluteUri);
    }, cancellationToken);

    public Task<IReadOnlyList<GalleryPage>> GetPagesAsync(string contentId, CancellationToken cancellationToken) => CallAsync<IReadOnlyList<GalleryPage>>(async (context, ct) =>
    {
        var pages = await PagesAsync(contentId, context, ct).ConfigureAwait(false);
        return pages.Select((p, i) => new GalleryPage(i + 1, p.Url, p.IsImage ? p.Url : null)).ToList();
    }, cancellationToken);

    public Task<Uri> GetImageUrlAsync(GalleryPage page, CancellationToken cancellationToken) =>
        page.ImageUrl is { } image
            ? Task.FromResult(image)
            : CallAsync((context, ct) => _source.GetImageUrlAsync(page.Url, context, ct), cancellationToken);

    /// <summary>Another image for a page of the site (e-hentai's «reload»); an image the listing named has none.</summary>
    public async Task<Uri?> RetryImageUrlAsync(GalleryPage page, Uri failed, CancellationToken cancellationToken)
    {
        if (page.ImageUrl is not null && page.ImageUrl == page.Url)
        {
            return null;
        }

        var again = await CallAsync((context, ct) => _source.RetryImageUrlAsync(page.Url, failed, context, ct), cancellationToken).ConfigureAwait(false);
        return again == failed ? null : again;
    }

    /// <summary>Signs in to the site with what was typed into its login's fields: the cookies to save (its manifest's <c>Login.CookiesKey</c>).</summary>
    public Task<string> LoginAsync(IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken) =>
        CallAsync((context, ct) => _source.LoginAsync(fields, context, ct), cancellationToken);

    /// <summary>The image through the source's HTTP (its domains and cookies, not its rate gate), read whole.</summary>
    public async Task<GalleryImage> OpenImageAsync(Uri url, CancellationToken cancellationToken)
    {
        var http = NewHttp();
        try
        {
            var (bytes, contentType) = await http.GetBytesAsync(url, MaxImageBytes, cancellationToken).ConfigureAwait(false);
            return new GalleryImage(new MemoryStream(bytes, writable: false), contentType, bytes.LongLength);
        }
        catch (Api.SourceException ex)
        {
            throw Failure(ex, http);
        }
    }

    private async Task<IReadOnlyList<Api.SourcePageLink>> PagesAsync(string contentId, Api.ISourceContext context, CancellationToken ct)
    {
        var url = Address(contentId);
        var pages = await _source.GetPagesAsync(url, context, ct).ConfigureAwait(false)
                    ?? throw new Api.SourceException($"{url} is not a release to download.");
        return pages.Pages ?? [];
    }

    private async Task<GallerySearchResult> ChaptersAsync(Uri series, int offset, string? language, Api.ISourceContext context, CancellationToken ct)
    {
        if (!_source.Manifest.Capabilities.HasFlag(Api.SourceCapabilities.Chapters))
        {
            return new GallerySearchResult([], null);
        }

        var chapters = await _source.GetChaptersAsync(new Api.SourceChapterQuery { Url = series, Language = language }, context, ct).ConfigureAwait(false) ?? [];
        var page = chapters.Where(c => c is { Url: not null }).Skip(offset).Take(ChaptersPerPage).Select(Content).ToList();
        var next = offset + ChaptersPerPage < chapters.Count ? $"c:{(offset + ChaptersPerPage).ToString(CultureInfo.InvariantCulture)}:{series.AbsoluteUri}" : null;
        return new GallerySearchResult(page, next);
    }

    /// <summary>A series is what lists releases: the metadata of a link whose series is the link itself.</summary>
    private bool IsSeries(Api.SourceMetadata metadata) =>
        _source.Manifest.Capabilities.HasFlag(Api.SourceCapabilities.Chapters) && metadata.Series?.Url is { } series && series == metadata.Url;

    private static Uri Address(string contentId) =>
        Uri.TryCreate(contentId, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url
            : throw new Api.SourceException($"'{contentId}' is not the address of a release.");

    private static string PageName(Uri url, int number)
    {
        var name = url.Segments.Length > 0 ? Uri.UnescapeDataString(url.Segments[^1]).Trim('/') : "";
        return name.Length > 0 ? name : number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The name of a release: its title, with the volume and chapter of a chapter and the chapter's own title.</summary>
    public static string ReleaseTitle(Api.SourceMetadata metadata)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(metadata.Volume))
        {
            parts.Add($"Vol. {metadata.Volume.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Chapter))
        {
            parts.Add($"Ch. {metadata.Chapter.Trim()}");
        }

        var chapter = metadata.Extra.TryGetValue("chapterTitle", out var own) && !string.IsNullOrWhiteSpace(own) ? own.Trim() : null;
        if (parts.Count == 0)
        {
            return chapter is null ? metadata.Title : $"{metadata.Title} — {chapter}";
        }

        return $"{metadata.Title} — {string.Join(' ', parts)}{(chapter is null ? "" : $": {chapter}")}";
    }

    /// <summary>A release with what the site says about it, under <see cref="GalleryProperties"/>.</summary>
    private ContentInfo Content(Api.SourceMetadata metadata)
    {
        var tags = metadata.Tags.Select(t => string.IsNullOrWhiteSpace(t.Namespace) ? t.Name : $"{t.Namespace}:{t.Name}");
        string[] Credits(string role) => metadata.Credits.Where(c => c.Role == role).Select(c => c.Name).ToArray();
        var properties = new Properties()
            .Add(GalleryProperties.Id, metadata.ExternalId)
            .Add(GalleryProperties.Url, metadata.Url.AbsoluteUri)
            .Add(GalleryProperties.Title, ReleaseTitle(metadata))
            .Add(GalleryProperties.OriginalTitle, metadata.AltTitles.FirstOrDefault()?.Title)
            .Add(GalleryProperties.Language, metadata.Language)
            .Add(GalleryProperties.Description, metadata.Description)
            .Add(GalleryProperties.Tags, tags)
            .Add(GalleryProperties.Authors, Credits(Api.SourceCreditRoles.Author))
            .Add(GalleryProperties.Artists, Credits(Api.SourceCreditRoles.Artist))
            .Add(GalleryProperties.Translators, Credits(Api.SourceCreditRoles.Translator))
            .Add(GalleryProperties.Groups, Credits(Api.SourceCreditRoles.Group))
            .Add(GalleryProperties.Characters, metadata.Characters)
            .Add(GalleryProperties.Series, metadata.Series?.Title)
            .Add(GalleryProperties.Volume, metadata.Volume)
            .Add(GalleryProperties.Chapter, metadata.Chapter)
            .Add(GalleryProperties.Pages, metadata.PageCount?.ToString(CultureInfo.InvariantCulture))
            .Add(GalleryProperties.Published, metadata.Published?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Add(GalleryProperties.Cover, metadata.CoverUrl?.AbsoluteUri)
            .Add(GalleryProperties.AgeRating, metadata.AgeRating is { } age ? ((int)age).ToString(CultureInfo.InvariantCulture) : null)
            .Add(GalleryProperties.Nsfw, (metadata.AgeRating is { } rated ? rated == Api.SourceAgeRating.Age18 : _source.Manifest.Nsfw) ? "true" : "false");
        foreach (var (key, value) in metadata.Extra)
        {
            properties.AddIfAbsent(key, value);
        }

        return new ContentInfo
        {
            ContentId = metadata.Url.AbsoluteUri,
            Title = ReleaseTitle(metadata),
            FileCount = metadata.PageCount,
            DedupKey = $"{Id}:{metadata.ExternalId}",
            InfoUrl = metadata.Url.AbsoluteUri,
            Properties = properties.Build(),
        };
    }

    /// <summary>A release a search found: what the catalog says, the full metadata comes with its link.</summary>
    private ContentInfo Content(Api.SourceCandidate candidate)
    {
        var properties = new Properties()
            .Add(GalleryProperties.Id, candidate.ExternalId)
            .Add(GalleryProperties.Url, candidate.Url.AbsoluteUri)
            .Add(GalleryProperties.Title, candidate.Title)
            .Add(GalleryProperties.Language, candidate.Language)
            .Add(GalleryProperties.Summary, candidate.Summary)
            .Add(GalleryProperties.Cover, candidate.CoverUrl?.AbsoluteUri);
        return new ContentInfo
        {
            ContentId = candidate.Url.AbsoluteUri,
            Title = string.IsNullOrWhiteSpace(candidate.Title) ? candidate.Url.AbsoluteUri : candidate.Title,
            DedupKey = $"{Id}:{candidate.ExternalId ?? candidate.Url.AbsoluteUri}",
            InfoUrl = candidate.Url.AbsoluteUri,
            Properties = properties.Build(),
        };
    }

    /// <summary>The flat properties of a release: blank values are left out, lists joined with <c>, </c>.</summary>
    private sealed class Properties
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Properties Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _values[key] = value.Trim();
            }

            return this;
        }

        public Properties Add(string key, IEnumerable<string> values) =>
            Add(key, string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.Ordinal)));

        public void AddIfAbsent(string key, string? value)
        {
            if (!_values.ContainsKey(key))
            {
                Add(key, value);
            }
        }

        public IReadOnlyDictionary<string, string> Build() => _values;
    }
}
