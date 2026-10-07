using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Entities
{
    /// <summary>
    /// A download started through Convy (by the agent). Links the downloader item to the
    /// category and placement wishes (sub-path, file selection) and keeps a unified status.
    /// </summary>
    [Index(nameof(Provider), nameof(ItemRef))]
    [Index(nameof(Status))]
    public class JobEntry
    {
        public int Id { get; set; }

        /// <summary>Downloader that owns the item (<c>qbittorrent</c>, <c>slskd</c>).</summary>
        [MaxLength(32)]
        public required string Provider { get; set; }

        /// <summary>Item reference inside the downloader (the info hash for torrents).</summary>
        [MaxLength(1024)]
        public required string ItemRef { get; set; }

        /// <summary>Category id from the configuration (e.g. <c>movies</c>).</summary>
        [MaxLength(64)]
        public required string Category { get; set; }

        /// <summary>Client category of the category (<c>qbittorrent_category</c>); the rules see it as <c>Category</c>.</summary>
        [MaxLength(256)]
        public string? ClientCategory { get; set; }

        /// <summary>Relative directory that replaces the download's root folder on placement.</summary>
        [MaxLength(1024)]
        public string? Subpath { get; set; }

        /// <summary>JSON array of selected paths relative to the result root; <c>null</c> for every file.</summary>
        public string? SelectedFilesJson { get; set; }

        [MaxLength(1024)]
        public required string Title { get; set; }

        public long? SizeBytes { get; set; }

        public int? FileCount { get; set; }

        [MaxLength(64)]
        public string? ResultId { get; set; }

        [MaxLength(128)]
        public string? SourceId { get; set; }

        /// <summary>Job status (<c>queued</c>, <c>downloading</c>, …). Concurrency token: writers never overwrite each other.</summary>
        [MaxLength(16)]
        [ConcurrencyCheck]
        public required string Status { get; set; }

        /// <summary>Name of the rule that placed the files (or is expected to).</summary>
        [MaxLength(256)]
        public string? Rule { get; set; }

        /// <summary>Directory the files were placed in (or are expected in).</summary>
        [MaxLength(2048)]
        public string? TargetPath { get; set; }

        [MaxLength(4096)]
        public string? Error { get; set; }

        public int PlacementAttempts { get; set; }

        /// <summary>Downloaded bytes when progress was last observed; drives stall detection.</summary>
        public long LastDownloadedBytes { get; set; }

        public DateTimeOffset LastProgressAt { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }
    }
}
