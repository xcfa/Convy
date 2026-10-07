using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Entities
{
	[PrimaryKey(nameof(Provider), nameof(InfoHash), nameof(FilePath))]
	public class FileEntry
	{
		/// <summary>Downloader that owns the item (<c>qbittorrent</c>, <c>slskd</c>).</summary>
		[MaxLength(32)]
		public required string Provider { get; set; }

		/// <summary>Item reference inside the downloader: the info hash for torrents.</summary>
		[MaxLength(1024)]
		public required string InfoHash { get; set; }

		[MaxLength(2048)]
		public required string FilePath { get; set; }

		[MaxLength(2048)]
		public required string TargetPath { get; set; }

		[MaxLength(1024)]
		public string? TorrentName { get; set; }

		public DateTimeOffset LinkedDate { get; set; }
	}
}
