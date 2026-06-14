using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Entities
{
    /// <summary>
    /// The last-known, persisted state of a torrent, used by the state tracker to
    /// emit only real changes across application restarts. Deliberately tiny — we
    /// only keep what the change definition needs (download completion + size).
    /// </summary>
    [PrimaryKey(nameof(InfoHash))]
    public class TorrentStateEntry
    {
        [MaxLength(128)]
        public required string InfoHash { get; set; }

        /// <summary>Whether the torrent was in a "downloaded" state when last seen.</summary>
        public bool IsDownloaded { get; set; }

        public long? Size { get; set; }

        public DateTimeOffset UpdatedDate { get; set; }
    }
}
