using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Convy.Models
{
	public class QBitTorrentTorrentData
	{
		[JsonPropertyName("name")]
		public string TorrentName { get; set; } = null!;

		[JsonPropertyName("category")]
		public string Category { get; set; } = null!;

		[JsonPropertyName("tags")]
		public List<string> Tags { get; set; } = [];

		[JsonPropertyName("contentPath")]
		public string ContentPath { get; set; } = null!;

		[JsonPropertyName("savePath")]
		public string SavePath { get; set; } = null!;

		[JsonPropertyName("numberOfFiles")]
		public int NumberOfFiles { get; set; }

		[JsonPropertyName("torrentSize")]
		public long TorrentSize { get; set; }

		[JsonPropertyName("currentTracker")]
		public string CurrentTracker { get; set; } = null!;

		[JsonPropertyName("infoHashV1")]
		public string InfoHashV1 { get; set; } = null!;

		[JsonPropertyName("infoHashV2")]
		public string InfoHashV2 { get; set; } = null!;

		[JsonPropertyName("torrentId")]
		public string TorrentId { get; set; } = null!;
	}
}
