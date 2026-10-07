using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Entities
{
    /// <summary>
    /// A search started by the agent: the category, the query variants and the ordered list
    /// of sources still to be searched by <c>search_next</c>. Times are unix seconds so SQLite
    /// can compare them.
    /// </summary>
    [Index(nameof(ExpiresAt))]
    public class SearchSessionEntry
    {
        [Key]
        [MaxLength(32)]
        public required string Id { get; set; }

        [MaxLength(64)]
        public required string Category { get; set; }

        /// <summary>JSON array of query variants.</summary>
        public required string QueriesJson { get; set; }

        /// <summary>JSON array of source ids in priority order.</summary>
        public required string SourcesJson { get; set; }

        /// <summary>Index in the source list where the next batch starts.</summary>
        public int NextSourceIndex { get; set; }

        public long CreatedAt { get; set; }

        public long ExpiresAt { get; set; }
    }

    /// <summary>One cached search result, addressed by its opaque id (<c>r_…</c>).</summary>
    [Index(nameof(SearchId), nameof(DedupKey))]
    [Index(nameof(ExpiresAt))]
    public class SearchResultEntry
    {
        [Key]
        [MaxLength(32)]
        public required string Id { get; set; }

        [MaxLength(32)]
        public required string SearchId { get; set; }

        [MaxLength(16)]
        public required string Protocol { get; set; }

        [MaxLength(1024)]
        public required string Title { get; set; }

        public long? SizeBytes { get; set; }

        public int? FileCount { get; set; }

        /// <summary>JSON object with seeders / free slot / queue length / speed.</summary>
        public required string AvailabilityJson { get; set; }

        /// <summary>JSON array of every source that returned this result, in priority order.</summary>
        public required string SourcesJson { get; set; }

        /// <summary>JSON array of the query variants that found it.</summary>
        public required string MatchedQueriesJson { get; set; }

        /// <summary>Identity across sources and variants (info hash, user + folder).</summary>
        [MaxLength(2048)]
        public string? DedupKey { get; set; }

        /// <summary>The source the result is listed and resolved through.</summary>
        [MaxLength(128)]
        public required string SourceId { get; set; }

        /// <summary>Source-specific opaque reference; never shown to the agent.</summary>
        public required string ContentId { get; set; }

        public long CreatedAt { get; set; }

        public long ExpiresAt { get; set; }
    }

    /// <summary>A cached file list of a search result.</summary>
    [Index(nameof(ExpiresAt))]
    public class FileListingEntry
    {
        [Key]
        [MaxLength(32)]
        public required string ResultId { get; set; }

        /// <summary>JSON array of <c>{ path, size }</c>.</summary>
        public required string FilesJson { get; set; }

        /// <summary>Raw .torrent bytes when the listing came from torrent metadata.</summary>
        public byte[]? TorrentFile { get; set; }

        public long CreatedAt { get; set; }

        public long ExpiresAt { get; set; }
    }
}
