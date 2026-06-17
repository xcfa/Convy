using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Entities
{
	[PrimaryKey(nameof(InfoHash), nameof(FilePath))]
	public class FileEntry
	{
		[MaxLength(128)]
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
