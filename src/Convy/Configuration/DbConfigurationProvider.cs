using Convy.Data.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;

namespace Convy.Configuration;

/// <summary>
/// Reads the <c>UserSettings</c> table and exposes each row as
/// <c>UserSettings:{Key}</c> in the configuration tree.
/// </summary>
public sealed class DbConfigurationProvider : ConfigurationProvider
{
    private readonly string _connectionString;

    public DbConfigurationProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var options = new DbContextOptionsBuilder<SettingsDbContext>()
                .UseSqlite(_connectionString)
                .Options;

            using var db = new SettingsDbContext(options);

            // ConfigurationProvider.Load is synchronous (it runs while the
            // configuration root is being built), so the read must be sync too.
            foreach (var entry in db.UserSettings.AsNoTracking())
            {
                data[$"UserSettings:{entry.Key}"] = entry.Value;
            }
        }
        catch (SqliteException)
        {
            // Table may not exist yet (pre-migration). Start with empty data.
        }

        Data = data;
    }

    /// <summary>Re-reads the table and fires change tokens.</summary>
    public void Reload()
    {
        Load();
        OnReload();
    }
}
