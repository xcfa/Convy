using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
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
public sealed class ExtensionSource : IGallerySource, IGalleryBrowser, ISourceUnit
{
    /// <summary>Chapters of a series go to a search this many at a time.</summary>
    public const int ChaptersPerPage = 100;

    /// <summary>The largest image a page may have.</summary>
    private const long MaxImageBytes = 64L * 1024 * 1024;

    private readonly Api.MetadataSource _source;
    private readonly ExtensionHost _host;
    private readonly ILogger _logger;
    private readonly RateGate _gate;
    private readonly object _lock = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private SourceCookieJar? _cookies;
    private (Uri Url, string Key, string Profile, CaptchaSolverClient Client)? _solver;

    public ExtensionSource(Api.MetadataSource source, string package, ExtensionHost host, ILogger logger)
    {
        _source = source;
        _host = host;
        _logger = logger;
        _gate = new RateGate(host.Time);
        Package = package;
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

    public string Id => _source.Manifest.Id;

    public string Name => _source.Manifest.Name;

    /// <summary>The extension package the source comes from.</summary>
    public string Package { get; }

    /// <summary>The source's manifest as the extension wrote it.</summary>
    public Api.SourceManifest Extension => _source.Manifest;

    public Protocol Protocol => Protocol.Gallery;

    public SourceStatus Status => SourceStatus.Ok;

    public string? StatusMessage => null;

    public GalleryManifest Manifest { get; }

    Api.SourceManifest ISourceUnit.Manifest => _source.Manifest;

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

    /// <summary>The image through the source's HTTP (its domains and cookies, not its rate gate), read whole.</summary>
    public async Task<GalleryImage> OpenImageAsync(Uri url, CancellationToken cancellationToken)
    {
        var entry = _host.Options.For(Id);
        var http = Http(Saved(entry), entry);
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

    /// <summary>
    /// Runs one call of the extension with a context made for it. A check the extension reported itself (the site's
    /// own captcha) is handed to the service of checks, and once passed the call runs once more. The extension's
    /// failures become <see cref="SourceException"/>s of the same kind; a check the service did not pass names the one
    /// that waits for a person (<see cref="SourceException.ChallengeId"/>).
    /// </summary>
    private async Task<T> CallAsync<T>(Func<Api.ISourceContext, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var entry = _host.Options.For(Id);
        var saved = Saved(entry);
        var http = Http(saved, entry);
        var context = new SourceContext(http, _host.Html, new SourceSettingsView(_source.Manifest, saved), new Journal(_logger, Id));
        try
        {
            try
            {
                return await call(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Api.SourceException ex) when (ex is { Kind: Api.SourceErrorKind.Challenge, ChallengeUrl: not null }
                                                 && http is { PassesChallenges: true, Challenge: null, ChallengeProblem: null })
            {
                if (!await http.PassSiteAsync(ex.ChallengeUrl, ex.ChallengeDoneWhen, cancellationToken).ConfigureAwait(false))
                {
                    ExceptionDispatchInfo.Throw(ex);
                }
            }

            return await call(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Api.SourceException ex)
        {
            throw Failure(ex, http);
        }
        catch (NotSupportedException ex)
        {
            throw new SourceException(SourceErrorKind.Error, ex.Message, ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SourceException))
        {
            _logger.LogError(ex, "Source {Source} of {Package} threw", Id, Package);
            throw new SourceException(SourceErrorKind.Error, $"{Name} failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>Convy's error for the extension's: of the same kind; a check says whether one waits for a person.</summary>
    private static SourceException Failure(Api.SourceException ex, SourceHttp http)
    {
        if (ex.Kind == Api.SourceErrorKind.AuthFailed)
        {
            return new SourceException(SourceErrorKind.AuthFailed, ex.Message, ex);
        }

        if (ex.Kind != Api.SourceErrorKind.Challenge)
        {
            return new SourceException(SourceErrorKind.Error, ex.Message, ex);
        }

        (string Message, string? Id) said = !http.PassesChallenges
            ? (ex.Message + " No service of checks is set up (captcha.url, captcha.key).", null)
            : http.ChallengeProblem is { } problem
                ? ($"{ex.Message} The service of checks failed: {problem}", null)
                : http.Challenge switch
                {
                    { Status: ChallengeStatus.Pending } pending => (ex.Message + " The check waits for someone to pass it.", pending.Id),
                    { } failed => ($"{ex.Message} The check was not passed{(failed.Message is { } why ? $": {why}" : "")}.", null),
                    null => (ex.Message, null),
                };
        return new SourceException(SourceErrorKind.Challenge, said.Message, ex)
        {
            ChallengeUrl = http.Challenge?.Url ?? ex.ChallengeUrl,
            ChallengeId = said.Id,
        };
    }

    /// <summary>HTTP of one call: the manifest's domains and those of its Url settings, the source's gate and cookies, its proxy and timeout.</summary>
    private SourceHttp Http(Dictionary<string, string> saved, ExtensionEntryOptions entry)
    {
        var manifest = _source.Manifest;
        var hosts = SettingValues.UrlHosts(manifest, saved);
        var limits = new SourceHttpLimits
        {
            RequestTimeout = TimeSpan.FromSeconds(Math.Max(5, entry.TimeoutSeconds)),
            FetchTimeout = TimeSpan.FromSeconds(Math.Max(5, entry.TimeoutSeconds)),
            ChallengeWait = CallBudget.Cut(TimeSpan.FromSeconds(Math.Clamp(_host.Captcha.CurrentValue.WaitSeconds, 0, 60))),
        };
        return new SourceHttp(this, hosts, _host.Transport(entry.Proxy), _gate, Cookies(manifest.Domains.Concat(hosts).ToList(), saved), new Journal(_logger, Id), limits,
            challenges: Solver());
    }

    /// <summary>The source's cookie jar: made again when its Cookies or Url settings change.</summary>
    private System.Net.CookieContainer Cookies(IReadOnlyList<string> domains, Dictionary<string, string> saved)
    {
        var seed = new StringBuilder();
        foreach (var setting in _source.Manifest.Settings.Where(s => s.Kind is Api.SourceSettingKind.Cookies or Api.SourceSettingKind.Url))
        {
            if (saved.TryGetValue(setting.Key, out var value))
            {
                seed.Append(setting.Key).Append('=').Append(value).Append('\n');
            }
        }

        lock (_lock)
        {
            if (_cookies is null || _cookies.Seed != seed.ToString())
            {
                _cookies = SourceCookieJar.Create(domains, seed.ToString());
            }

            return _cookies.Container;
        }
    }

    /// <summary>The service of checks in this source's own profile there (<c>&lt;domain&gt;#convy.&lt;id&gt;</c>); null — none set up.</summary>
    private CaptchaSolverClient? Solver()
    {
        var options = _host.Captcha.CurrentValue;
        if (options.Upstream is not { } upstream)
        {
            return null;
        }

        var profile = options.ProfileFor(Id);
        lock (_lock)
        {
            if (_solver is not { } known || known.Url != upstream.Url || known.Key != upstream.Key || known.Profile != profile)
            {
                _solver = known = (upstream.Url, upstream.Key, profile, new CaptchaSolverClient(_host.CaptchaHttp, upstream.Url, upstream.Key, profile));
            }

            return known.Client;
        }
    }

    /// <summary>The values of <c>values</c> the manifest has a setting for, checked and normalized by its kind; others are left out with a warning.</summary>
    private Dictionary<string, string> Saved(ExtensionEntryOptions entry)
    {
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entry.Values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var setting = _source.Manifest.Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
            if (setting is null)
            {
                WarnOnce(key, value, $"extensions.settings.{Id}.values.{key}: {Name} has no such setting.");
                continue;
            }

            if (SettingValues.Normalize(setting, value.Trim(), out var error) is { } normalized)
            {
                saved[setting.Key] = normalized;
            }
            else
            {
                WarnOnce(key, value, $"extensions.settings.{Id}.values.{key} is left out: {error}");
            }
        }

        return saved;
    }

    /// <summary>A warning about a setting's value, once for each value: settings are read on every call.</summary>
    private void WarnOnce(string key, string value, string warning)
    {
        lock (_lock)
        {
            if (!_warned.Add($"{key}\n{value}"))
            {
                return;
            }
        }

        _logger.LogWarning("{Warning}", warning);
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

    /// <summary>The source's notes and the answers of its requests, into Convy's log under the source's id.</summary>
    private sealed class Journal(ILogger logger, string source) : Api.ISourceLog, ISourceHttpJournal
    {
        public void Write(Api.SourceLogLevel level, string message, object? data = null) =>
            logger.Log(level switch
            {
                Api.SourceLogLevel.Warning => LogLevel.Warning,
                Api.SourceLogLevel.Error => LogLevel.Error,
                _ => LogLevel.Information,
            }, "[{Source}] {Message} {@Data}", source, message, data);

        public Task ResponseAsync(string request, int? statusCode, string body, CancellationToken ct)
        {
            logger.LogDebug("[{Source}] {Request} → {Status}", source, request, statusCode);
            return Task.CompletedTask;
        }
    }
}
