using Microsoft.Extensions.Configuration;

namespace Convy.Services.Downloaders.Opds;

/// <summary>
/// The <c>opds</c> section of configuration.yml: where the files of a source that has Convy fetch them (an OPDS catalog's
/// books) are stored. The catalogs themselves are sources of the OPDS extension.
/// </summary>
public sealed class OpdsOptions
{
    public const string SectionName = "Opds";

    /// <summary>
    /// Where Convy stores downloaded books before placing them, as a path inside Convy. Must be on
    /// the same filesystem as the rule paths (hard links). Without it books cannot be downloaded.
    /// </summary>
    [ConfigurationKeyName("downloads_path")]
    public string? DownloadsPath { get; set; }
}
