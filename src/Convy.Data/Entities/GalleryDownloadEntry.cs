using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Entities
{
    /// <summary>
    /// A gallery Convy downloads itself page by page from a plugin source into a CBZ: where it goes and how far the
    /// download got. Survives restarts, so unfinished downloads are picked up again.
    /// </summary>
    [PrimaryKey(nameof(ItemRef))]
    [Index(nameof(State))]
    public class GalleryDownloadEntry
    {
        /// <summary><c>&lt;source&gt;:&lt;content id&gt;</c>, e.g. <c>e-hentai:123456/abcdef0123</c>.</summary>
        [MaxLength(512)]
        public required string ItemRef { get; set; }

        [MaxLength(128)]
        public required string SourceId { get; set; }

        [MaxLength(512)]
        public required string ContentId { get; set; }

        /// <summary>The release's name as the source gave it.</summary>
        [MaxLength(1024)]
        public required string Title { get; set; }

        /// <summary>Folder the archive is stored in, below <see cref="SavePath"/>.</summary>
        [MaxLength(512)]
        public required string Folder { get; set; }

        /// <summary>The archive's file name inside <see cref="Folder"/>.</summary>
        [MaxLength(512)]
        public required string FileName { get; set; }

        /// <summary>Directory the folder is created in (also holds the pages while downloading).</summary>
        [MaxLength(2048)]
        public required string SavePath { get; set; }

        /// <summary>Client category of the job (the rules see it as <c>Category</c>).</summary>
        [MaxLength(256)]
        public string? Category { get; set; }

        /// <summary>How many pages the gallery has, once listed; <c>null</c> before.</summary>
        public int? PagesTotal { get; set; }

        /// <summary>Pages fetched so far.</summary>
        public int PagesDone { get; set; }

        /// <summary>Bytes of the fetched pages, or of the archive once complete.</summary>
        public long Bytes { get; set; }

        /// <summary>
        /// <c>queued</c>, <c>downloading</c>, <c>retrying</c>, <c>completed</c>, <c>failed</c> or <c>cancelled</c>.
        /// Concurrency token: the worker and a cancellation never overwrite each other.
        /// </summary>
        [MaxLength(16)]
        [ConcurrencyCheck]
        public required string State { get; set; }

        [MaxLength(4096)]
        public string? Error { get; set; }

        /// <summary>Failed attempts of the current run.</summary>
        public int Attempts { get; set; }

        /// <summary>When a retrying download is tried again, in unix seconds.</summary>
        public long? NextAttemptAt { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
