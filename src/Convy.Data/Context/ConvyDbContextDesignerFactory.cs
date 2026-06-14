using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Convy.Data.Context
{
	internal class ConvyDbContextDesignerFactory : IDesignTimeDbContextFactory<ConvyDbContext>
	{
		public ConvyDbContext CreateDbContext(string[] args)
		{
			var options = new DbContextOptionsBuilder<ConvyDbContext>()
					.UseSqlite(string.Empty)
					.Options;

			return new ConvyDbContext(options);
		}
	}
}
