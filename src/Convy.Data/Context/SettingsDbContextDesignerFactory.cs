using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Convy.Data.Context
{
	internal class SettingsDbContextDesignerFactory : IDesignTimeDbContextFactory<SettingsDbContext>
	{
		public SettingsDbContext CreateDbContext(string[] args)
		{
			var options = new DbContextOptionsBuilder<SettingsDbContext>()
					.UseSqlite(string.Empty, b => b.MigrationsHistoryTable("__EFMigrationsHistorySettings"))
					.Options;

			return new SettingsDbContext(options);
		}
	}
}
