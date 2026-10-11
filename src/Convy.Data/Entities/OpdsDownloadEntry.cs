using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Entities
{
    /// <summary>
    /// A book Convy downloads itself from an OPDS catalog: its files, where they go and how far
    /// the download got. Survives restarts, so unfinished downloads are picked up again.
    /// </summary>
    [PrimaryKey(nameof(ItemRef))]
    [Index(nameof(State))]
    public class OpdsDownloadEntry
    {
        /// <summary><c>&lt;catalog&gt;/&lt;book key&gt;</c>, e.g. <c>flibusta/b93857</c>.</summary>
        [MaxLength(256)]
        public required string ItemRef { get; set; }

        [MaxLength(64)]
        public required string CatalogId { get; set; }

        /// <summary>Folder the files are stored in, below <see cref="SavePath"/>.</summary>
        [MaxLength(512)]
        public required string Folder { get; set; }

        /// <summary>Directory the book's folder is created in.</summary>
        [MaxLength(2048)]
        public required string SavePath { get; set; }

        /// <summary>Client category of the job (the rules see it as <c>Category</c>).</summary>
        [MaxLength(256)]
        public string? Category { get; set; }

        /// <summary>JSON array of the book's files with their state.</summary>
        public required string FilesJson { get; set; }

        /// <summary>When the best format fails for good, the next preferred one is tried.</summary>
        public bool Fallback { get; set; }

        /// <summary>
        /// <c>queued</c>, <c>downloading</c>, <c>retrying</c>, <c>completed</c>, <c>failed</c> or
        /// <c>cancelled</c>. Concurrency token: the worker and a cancellation never overwrite each other.
        /// </summary>
        [MaxLength(16)]
        [ConcurrencyCheck]
        public required string State { get; set; }

        [MaxLength(4096)]
        public string? Error { get; set; }

        /// <summary>Failed attempts of the current file.</summary>
        public int Attempts { get; set; }

        /// <summary>When a retrying download is tried again, in unix seconds (SQLite can compare it).</summary>
        public long? NextAttemptAt { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
