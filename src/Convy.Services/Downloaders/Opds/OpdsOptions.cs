using System.Text.RegularExpressions;
using Convy.Sources.Opds;
using Microsoft.Extensions.Configuration;

namespace Convy.Services.Downloaders.Opds;

/// <summary>The <c>opds</c> section of configuration.yml: catalogs to search and where their books are stored.</summary>
public sealed partial class OpdsOptions
{
    public const string SectionName = "Opds";

    /// <summary>
    /// Where Convy stores downloaded books before placing them, as a path inside Convy. Must be on
    /// the same filesystem as the rule paths (hard links). Without it books cannot be downloaded.
    /// </summary>
    [ConfigurationKeyName("downloads_path")]
    public string? DownloadsPath { get; set; }

    [ConfigurationKeyName("catalogs")]
    public List<OpdsCatalogOptions> Catalogs { get; set; } = [];

    /// <summary>The usable catalogs; problems with the others are returned in <paramref name="problems"/>.</summary>
    public IReadOnlyList<(OpdsCatalog Catalog, OpdsCatalogOptions Options)> Resolve(out IReadOnlyList<string> problems)
    {
        var catalogs = new List<(OpdsCatalog, OpdsCatalogOptions)>();
        var found = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (options, index) in Catalogs.Select((c, i) => (c, i + 1)))
        {
            if (options.ToCatalog(out var problem) is not { } catalog)
            {
                found.Add($"OPDS catalog #{index}: {problem}");
            }
            else if (!ids.Add(catalog.Id))
            {
                found.Add($"OPDS catalog #{index}: id '{catalog.Id}' is used twice.");
            }
            else
            {
                catalogs.Add((catalog, options));
            }
        }

        problems = found;
        return catalogs;
    }
}

/// <summary>One OPDS catalog in configuration.yml.</summary>
public sealed partial class OpdsCatalogOptions
{
    /// <summary>Short id (<c>flibusta</c>); the source id is <c>opds:&lt;id&gt;</c>.</summary>
    [ConfigurationKeyName("id")]
    public string? Id { get; set; }

    [ConfigurationKeyName("name")]
    public string? Name { get; set; }

    /// <summary><c>flibusta</c> (Flibusta and sites on its engine: search URLs and author pages preset) or <c>opds</c>.</summary>
    [ConfigurationKeyName("type")]
    public string Type { get; set; } = "opds";

    /// <summary>The catalog's root feed. For <c>flibusta</c> defaults to <c>https://flibusta.site/opds</c>.</summary>
    [ConfigurationKeyName("url")]
    public string? Url { get; set; }

    /// <summary>Book search address with <c>{searchTerms}</c>; by default the one the catalog announces.</summary>
    [ConfigurationKeyName("search_url")]
    public string? SearchUrl { get; set; }

    /// <summary>Author search address with <c>{searchTerms}</c>; without it only titles are searched.</summary>
    [ConfigurationKeyName("author_search_url")]
    public string? AuthorSearchUrl { get; set; }

    /// <summary>Formats in order of preference; the first one a book has is downloaded by default.</summary>
    [ConfigurationKeyName("formats")]
    public List<string>? Formats { get; set; }

    /// <summary>Store the <c>.fb2</c> inside a <c>.fb2.zip</c>.</summary>
    [ConfigurationKeyName("unzip_fb2")]
    public bool UnzipFb2 { get; set; } = true;

    /// <summary>How long one search may take; Flibusta often needs a minute.</summary>
    [ConfigurationKeyName("timeout_sec")]
    public double TimeoutSeconds { get; set; } = 120;

    /// <summary>Pages (20 books each on Flibusta) of an author's books read for one author found by a search.</summary>
    [ConfigurationKeyName("author_pages")]
    public int AuthorPages { get; set; } = 3;

    /// <summary>Proxy for this catalog, e.g. <c>socks5://tor:9050</c> or <c>http://proxy:3128</c>.</summary>
    [ConfigurationKeyName("proxy")]
    public string? Proxy { get; set; }

    /// <summary>User name for catalogs that require HTTP basic authentication.</summary>
    [ConfigurationKeyName("username")]
    public string? Username { get; set; }

    /// <summary>Password for basic authentication; pass it as a Docker secret.</summary>
    [ConfigurationKeyName("password")]
    public string? Password { get; set; }

    public bool IsFlibusta => string.Equals(Type, "flibusta", StringComparison.OrdinalIgnoreCase);

    /// <summary>The catalog these settings describe, or <c>null</c> with the reason.</summary>
    public OpdsCatalog? ToCatalog(out string? problem)
    {
        problem = null;
        var id = Id?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id) || !IdPattern().IsMatch(id))
        {
            problem = "id must be lower-case letters, digits and dashes.";
            return null;
        }

        if (!IsFlibusta && !string.Equals(Type, "opds", StringComparison.OrdinalIgnoreCase))
        {
            problem = $"type must be 'flibusta' or 'opds', not '{Type}'.";
            return null;
        }

        var url = string.IsNullOrWhiteSpace(Url) && IsFlibusta ? "https://flibusta.site/opds" : Url?.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
        {
            problem = "url must be an absolute http(s) address of the catalog's root feed.";
            return null;
        }

        if (Proxy is { Length: > 0 } && !Uri.TryCreate(Proxy, UriKind.Absolute, out _))
        {
            problem = $"proxy '{Proxy}' is not an address such as socks5://host:9050.";
            return null;
        }

        var formats = (Formats ?? []).Select(f => f.Trim().TrimStart('.').ToLowerInvariant()).Where(f => f.Length > 0).Distinct().ToList();
        return new OpdsCatalog
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(Name) ? (IsFlibusta ? "Flibusta" : id) : Name.Trim(),
            Url = root,
            SearchUrl = Blank(SearchUrl) ?? (IsFlibusta ? "/opds/search?searchType=books&searchTerm={searchTerms}" : null),
            AuthorSearchUrl = Blank(AuthorSearchUrl) ?? (IsFlibusta ? "/opds/search?searchType=authors&searchTerm={searchTerms}" : null),
            FlibustaAuthorPages = IsFlibusta,
            Formats = formats.Count > 0 ? formats : ["epub", "fb2"],
            UnzipFb2 = UnzipFb2,
            Timeout = TimeSpan.FromSeconds(Math.Max(5, TimeoutSeconds)),
            AuthorPages = Math.Clamp(AuthorPages, 1, 20),
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();
}
