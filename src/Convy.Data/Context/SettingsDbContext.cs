using Convy.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Convy.Data.Context
{
	/// <summary>
	/// Dedicated context for user-overridable settings. Kept separate from
	/// <see cref="ConvyDbContext"/> so settings persistence has its own model
	/// and migration history, independent of the media/torrent schema.
	/// </summary>
	public class SettingsDbContext : DbContext
	{
		public DbSet<UserSettingEntry> UserSettings => Set<UserSettingEntry>();

		public SettingsDbContext(DbContextOptions<SettingsDbContext> options)
			: base(options)
		{

		}
	}
}
