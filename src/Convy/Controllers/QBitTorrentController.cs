using Microsoft.AspNetCore.Mvc;
using Convy.Models;
using System.Threading.Tasks;
using System;
using System.IO;

namespace Convy.Controllers
{
	[Route("api/v1")]
	public class QBitTorrentController: ControllerBase
	{
		[HttpPost("added")]
		public async Task TorrentAdded([FromBody] QBitTorrentTorrentData data)
		{
			using var reader = new StreamReader(Request.Body);
			var raw = await reader.ReadToEndAsync();
			Console.WriteLine(raw);
		}
	}
}
