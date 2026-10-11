using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Context
{
	public class ConvyDbContext: DbContext
	{
		public DbSet<FileEntry> FileEntries => Set<FileEntry>();

		public DbSet<DownloadStateEntry> DownloadStates => Set<DownloadStateEntry>();

		public DbSet<JobEntry> Jobs => Set<JobEntry>();

		public DbSet<SearchSessionEntry> SearchSessions => Set<SearchSessionEntry>();

		public DbSet<SearchResultEntry> SearchResults => Set<SearchResultEntry>();

		public DbSet<FileListingEntry> FileListings => Set<FileListingEntry>();

		public DbSet<WebhookEntry> Webhooks => Set<WebhookEntry>();

		public ConvyDbContext(DbContextOptions<ConvyDbContext> context)
			: base(context)
		{

		}
	}
}
