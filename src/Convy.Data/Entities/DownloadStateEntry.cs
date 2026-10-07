using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Entities
{
    /// <summary>
    /// The last-known, persisted state of a download item, used by the state tracker to
    /// emit only real changes across application restarts. Deliberately tiny — we only
    /// keep what the change definition needs (download completion + size).
    /// </summary>
    [PrimaryKey(nameof(Provider), nameof(ItemRef))]
    public class DownloadStateEntry
    {
        /// <summary>Downloader that owns the item (<c>qbittorrent</c>, <c>slskd</c>).</summary>
        [MaxLength(32)]
        public required string Provider { get; set; }

        /// <summary>Item reference inside the downloader (the info hash for torrents).</summary>
        [MaxLength(1024)]
        public required string ItemRef { get; set; }

        /// <summary>Whether the item was in a "downloaded" state when last seen.</summary>
        public bool IsDownloaded { get; set; }

        public long? Size { get; set; }

        public DateTimeOffset UpdatedDate { get; set; }
    }
}
