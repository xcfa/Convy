using System;
using System.Threading;
using System.Threading.Tasks;
using Convy.Services;
using Convy.Services.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services
{
	public sealed class QBitTorrentSyncService : BackgroundService
	{
		private readonly QBitTorrentCommunicationService _communication;
		private readonly QBitTorrentConnectionSettings _settings;
		private readonly ILogger<QBitTorrentSyncService> _logger;

		public QBitTorrentSyncService(
			QBitTorrentCommunicationService communication,
			IOptions<QBitTorrentConnectionSettings> settings,
			ILogger<QBitTorrentSyncService> logger)
		{
			_communication = communication;
			_settings = settings.Value;
			_logger = logger;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (_settings.SyncInterval == TimeSpan.Zero)
			{
				_logger.LogInformation("Sync interval is zero; qBittorrent sync disabled.");

				return;
			}

			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await _communication.SyncFilesAsync(stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "qBittorrent sync cycle failed.");
				}

				try
				{
					await Task.Delay(_settings.SyncInterval, stoppingToken);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}
	}
}
