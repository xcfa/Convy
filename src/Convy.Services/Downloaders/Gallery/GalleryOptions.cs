using Microsoft.Extensions.Configuration;

namespace Convy.Services.Downloaders.Gallery;

/// <summary>The <c>gallery</c> section of configuration.yml: where galleries from plugin sources are downloaded.</summary>
public sealed class GalleryOptions
{
    public const string SectionName = "Gallery";

    /// <summary>
    /// Where Convy stores the CBZ archives before placing them, as a path inside Convy. Must be on the same filesystem
    /// as the rule paths (hard links). Without it galleries can be found but not downloaded.
    /// </summary>
    [ConfigurationKeyName("downloads_path")]
    public string? DownloadsPath { get; set; }

    /// <summary>The category a job started through the REST API gets when the request names none.</summary>
    [ConfigurationKeyName("category")]
    public string Category { get; set; } = "other";

    /// <summary>Pages downloaded at once within one gallery.</summary>
    [ConfigurationKeyName("parallel_pages")]
    public int ParallelPages { get; set; } = 2;

    /// <summary>How long fetching one page may take before it counts as failed.</summary>
    [ConfigurationKeyName("page_timeout_sec")]
    public double PageTimeoutSeconds { get; set; } = 120;
}
