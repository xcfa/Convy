using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Context
{
	public class ConvyDbContext: DbContext
	{
		public DbSet<FileEntry> FileEntries => Set<FileEntry>();

		public DbSet<DownloadStateEntry> DownloadStates => Set<DownloadStateEntry>();

		public ConvyDbContext(DbContextOptions<ConvyDbContext> context)
			: base(context)
		{

		}
	}
}
