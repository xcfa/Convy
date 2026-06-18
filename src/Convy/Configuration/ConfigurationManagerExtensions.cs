using System;
using Microsoft.Extensions.Configuration;

namespace Convy.Configuration
{
	public static class ConfigurationManagerExtensions
	{
		/// <summary>
		/// Registers the SQLite <c>UserSettings</c> table as a configuration source
		/// and returns it, so callers can trigger
		/// <see cref="DbConfigurationProvider.Reload"/> after writing settings.
		/// </summary>
		public static DbConfigurationSource AddDbConfiguration(
			this ConfigurationManager manager)
		{
			var connectionString = manager.GetConnectionString("SQLite")
				?? throw new InvalidOperationException("Missing 'SQLite' connection string.");

			var source = new DbConfigurationSource(connectionString);

			IConfigurationBuilder configBuilder = manager;
			configBuilder.Add(source);

			return source;
		}
	}
}
