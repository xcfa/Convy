using System.Text.Json;
using Shizuka.Sources.Abstractions;

namespace Convy.Tests.TestExtension;

/// <summary>
/// A gallery site at <c>https://gallery.test</c> with a JSON API, as the tests fake it: galleries at <c>/g/{id}</c>,
/// series at <c>/s/{id}</c> whose chapters are galleries; a gallery whose archive the site has is downloaded as that.
/// </summary>
public sealed class TestGallery : MetadataSource
{
    public const string Site = "https://gallery.test";

    public override SourceManifest Manifest { get; } = new()
    {
        Id = "test-gallery",
        Name = "Test gallery",
        Domains = ["gallery.test"],
        Capabilities = SourceCapabilities.Resolve | SourceCapabilities.Browse | SourceCapabilities.Download | SourceCapabilities.Chapters | SourceCapabilities.Archive,
        Nsfw = true,
        Languages = ["ja", "en"],
        DefaultAgeRating = SourceAgeRating.Age18,
        Settings =
        [
            new SourceSettingDefinition { Key = "token", Label = "Token", Kind = SourceSettingKind.Secret },
            new SourceSettingDefinition { Key = "cookies", Label = "Cookies", Kind = SourceSettingKind.Cookies },
        ],
        BrowseFilters =
        [
            new SourceBrowseFilter { Key = "category", Label = "Category", Options = [new SourceBrowseOption("manga", "Manga"), new SourceBrowseOption("doujinshi", "Doujinshi")] },
        ],
        RateLimit = new SourceRateLimit(50, TimeSpan.FromSeconds(1)),
    };

    public override async Task<SourceMetadata?> ResolveAsync(Uri url, ISourceContext context, CancellationToken ct)
    {
        var path = url.AbsolutePath.Trim('/').Split('/');
        if (path is not [("g" or "s") and var kind, var id])
            return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Site}/api/{kind}/{id}");
        request.Headers.TryAddWithoutValidation("X-Token", context.Settings.Get("token") ?? "");
        var response = await context.Http.SendAsync(request, ct);
        if (response.StatusCode == 404)
            return null;
        if (response.IsChallenge)
            throw SourceException.Challenge(response.Url, "gallery.test asks to prove this is not a robot.");
        if (response.Headers.FirstOrDefault(h => string.Equals(h.Key, "X-Captcha", StringComparison.OrdinalIgnoreCase)).Value is [var captcha, ..])
            throw SourceException.Challenge(new Uri(captcha), "The site asks for its captcha.", new SourceChallengeDoneWhen { UrlLeaves = true });
        if (response.StatusCode == 403)
            throw new SourceException("The token was refused.") { Kind = SourceErrorKind.AuthFailed };
        if (!response.IsSuccess)
            throw new SourceException($"The site answered {response.StatusCode}.");
        using var json = JsonDocument.Parse(response.Body);
        var g = json.RootElement;
        var self = new Uri($"{Site}/{kind}/{id}");
        return new SourceMetadata
        {
            ExternalId = id,
            Url = self,
            Title = g.GetProperty("title").GetString()!,
            Language = g.TryGetProperty("language", out var language) ? language.GetString() : null,
            Tags = Strings(g, "tags").Select(t => t.Split(':', 2) is [var ns, var name] ? new SourceTag(name, ns) : new SourceTag(t)).ToList(),
            Credits = Strings(g, "artists").Select(a => new SourceCredit(a, SourceCreditRoles.Artist))
                .Concat(Strings(g, "circles").Select(c => new SourceCredit(c, "circle"))).ToList(),
            PageCount = g.TryGetProperty("pages", out var pages) ? pages.GetInt32() : null,
            Chapter = g.TryGetProperty("chapter", out var chapter) ? chapter.GetString() : null,
            Series = kind == "s"
                ? new SourceSeries { Title = g.GetProperty("title").GetString()!, ExternalId = id, Url = self }
                : g.TryGetProperty("series", out var series) ? new SourceSeries { Title = series.GetString()! } : null,
        };
    }

    public override async Task<SourcePage> BrowseAsync(SourceBrowseQuery query, ISourceContext context, CancellationToken ct)
    {
        var url = $"{Site}/api/search?q={Uri.EscapeDataString(query.Text ?? "")}&page={query.Page ?? "1"}"
                  + (query.Language is null ? "" : $"&lang={query.Language}")
                  + string.Concat(query.Filters.SelectMany(f => f.Value.Select(v => $"&{f.Key}={Uri.EscapeDataString(v)}")));
        using var json = await context.Http.GetJsonAsync(new Uri(url), ct);
        var items = json.RootElement.GetProperty("items").EnumerateArray().Select(i =>
        {
            var series = i.TryGetProperty("series", out var s) && s.GetBoolean();
            var id = i.GetProperty("id").GetString()!;
            return new SourceCandidate
            {
                Url = new Uri($"{Site}/{(series ? "s" : "g")}/{id}"),
                Title = i.GetProperty("title").GetString()!,
                ExternalId = id,
                IsSeries = series,
            };
        }).ToList();
        var next = json.RootElement.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return new SourcePage { Items = items, NextPage = next };
    }

    public override async Task<IReadOnlyList<SourceCandidate>> GetChaptersAsync(SourceChapterQuery query, ISourceContext context, CancellationToken ct)
    {
        using var json = await context.Http.GetJsonAsync(new Uri($"{Site}/api{query.Url.AbsolutePath}/chapters"), ct);
        return json.RootElement.EnumerateArray()
            .Select(c => new SourceCandidate { Url = new Uri($"{Site}/g/{c.GetProperty("id").GetString()}"), Title = c.GetProperty("title").GetString()!, ExternalId = c.GetProperty("id").GetString() })
            .ToList();
    }

    /// <summary>The listing names the image of a page when the site has it at hand; the others are pages of the site.</summary>
    public override async Task<SourcePages?> GetPagesAsync(Uri url, ISourceContext context, CancellationToken ct)
    {
        if (url.AbsolutePath.Trim('/').Split('/') is not ["g", var id])
            return null;
        using var json = await context.Http.GetJsonAsync(new Uri($"{Site}/api/g/{id}/pages"), ct);
        return new SourcePages
        {
            Pages = json.RootElement.EnumerateArray()
                .Select((p, i) => p.TryGetProperty("image", out var image)
                    ? new SourcePageLink(new Uri(image.GetString()!))
                    : new SourcePageLink(new Uri($"{Site}/g/{id}/{i + 1}"), IsImage: false))
                .ToList(),
        };
    }

    /// <summary>The archive at the address <c>/api/g/{id}/archive</c> names, with the token; none (404) — the pages.</summary>
    public override async Task<SourceArchive?> GetArchiveAsync(Uri url, ISourceContext context, IProgress<SourceProgress> progress, CancellationToken ct)
    {
        if (url.AbsolutePath.Trim('/').Split('/') is not ["g", var id])
            return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Site}/api/g/{id}/archive");
        var response = await context.Http.SendAsync(request, ct);
        if (response.StatusCode == 404)
            return null;
        if (!response.IsSuccess)
            throw new SourceException($"The site answered {response.StatusCode} to the archive.");
        using var json = JsonDocument.Parse(response.Body);
        return new SourceArchive
        {
            Url = new Uri(json.RootElement.GetProperty("url").GetString()!),
            Headers = context.Settings.Get("token") is { } token ? new Dictionary<string, string> { ["X-Token"] = token } : null,
            Pages = json.RootElement.TryGetProperty("pages", out var pages) ? pages.GetInt32() : null,
        };
    }

    public override async Task<Uri> GetImageUrlAsync(Uri page, ISourceContext context, CancellationToken ct)
    {
        using var json = await context.Http.GetJsonAsync(new Uri($"{Site}/api{page.AbsolutePath}"), ct);
        return new Uri(json.RootElement.GetProperty("image").GetString()!);
    }

    /// <summary>A broken image server: the same image from the good one.</summary>
    public override Task<Uri> RetryImageUrlAsync(Uri page, Uri failed, ISourceContext context, CancellationToken ct) =>
        failed.AbsolutePath.Contains("/broken/", StringComparison.Ordinal)
            ? Task.FromResult(new Uri(failed.AbsoluteUri.Replace("/broken/", "/img/", StringComparison.Ordinal)))
            : GetImageUrlAsync(page, context, ct);

    private static IEnumerable<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var values) ? values.EnumerateArray().Select(v => v.GetString()!) : [];
}

/// <summary>A source that only knows metadata: Convy has nothing to download from it.</summary>
public sealed class TestCatalog : MetadataSource
{
    public override SourceManifest Manifest { get; } = new()
    {
        Id = "test-catalog",
        Name = "Test catalog",
        Domains = ["catalog.test"],
    };
}
