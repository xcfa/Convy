using Microsoft.Extensions.Configuration;

namespace Convy.Services.Media;

/// <summary>The <c>categories</c> section of configuration.yml, keyed by category id.</summary>
public sealed class CategoriesOptions
{
    public Dictionary<string, CategoryOptions> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A category: where to search and how to label the download. It has no path: the rules
/// decide where files go.
/// </summary>
public sealed class CategoryOptions
{
    /// <summary>Shown to the agent so it picks the right category.</summary>
    public string? Description { get; set; }

    /// <summary>Shown to the agent so it builds <c>subpath</c> consistently, e.g. "Title (Year)".</summary>
    [ConfigurationKeyName("path_hint")]
    public string? PathHint { get; set; }

    /// <summary>Category set on torrents; visible to the rules as <c>Category</c> for every job of this category.</summary>
    [ConfigurationKeyName("qbittorrent_category")]
    public string? QbittorrentCategory { get; set; }

    /// <summary>Source ids in priority order; all active sources by name when omitted.</summary>
    public List<string>? Sources { get; set; }

    /// <summary>Read by the Prowlarr provider only.</summary>
    public ProwlarrCategoryOptions? Prowlarr { get; set; }

    /// <summary>Read by the Soulseek provider only.</summary>
    public SoulseekCategoryOptions? Soulseek { get; set; }
}

public sealed class ProwlarrCategoryOptions
{
    /// <summary>Newznab category ids, e.g. 2000 (movies), 3000 (audio), 5000 (TV).</summary>
    public List<int>? Categories { get; set; }
}

public sealed class SoulseekCategoryOptions
{
    /// <summary>File extensions to keep (without dot), e.g. flac, mp3.</summary>
    public List<string>? Extensions { get; set; }
}

/// <summary>The <c>search</c> section of configuration.yml.</summary>
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>Sources searched per step (<c>search</c>, <c>search_next</c>).</summary>
    [ConfigurationKeyName("batch_size")]
    public int BatchSize { get; set; } = 3;

    /// <summary>Query variants used per search; extra ones are dropped.</summary>
    [ConfigurationKeyName("max_query_variants")]
    public int MaxQueryVariants { get; set; } = 3;

    /// <summary>Timeout of one source request.</summary>
    [ConfigurationKeyName("source_timeout_sec")]
    public double SourceTimeoutSeconds { get; set; } = 20;

    /// <summary>Results returned per step; the rest is dropped with a "shown N of M" note.</summary>
    [ConfigurationKeyName("max_results")]
    public int MaxResults { get; set; } = 30;

    /// <summary>How long search results and file lists stay addressable.</summary>
    [ConfigurationKeyName("cache_ttl_hours")]
    public double CacheTtlHours { get; set; } = 6;
}

/// <summary>The <c>files</c> section of configuration.yml.</summary>
public sealed class FilesOptions
{
    public const string SectionName = "Files";

    /// <summary>How long <c>list_files</c> waits for a file list (torrent metadata, peer folder).</summary>
    [ConfigurationKeyName("metadata_timeout_sec")]
    public double MetadataTimeoutSeconds { get; set; } = 15;

    /// <summary>Lines per <c>list_files</c> page.</summary>
    [ConfigurationKeyName("max_entries")]
    public int MaxEntries { get; set; } = 100;
}
