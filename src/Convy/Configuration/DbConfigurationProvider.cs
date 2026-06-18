using Convy.Data.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Convy.Configuration;

/// <summary>
/// Reads the <c>UserSettings</c> table and exposes each row as
/// <c>UserSettings:{Key}</c> in the configuration tree. The database is read
/// asynchronously via <see cref="ReloadAsync"/>; the synchronous
/// <see cref="Load"/> required by the configuration contract is intentionally a
/// no-op so that building configuration never blocks on database I/O.
/// </summary>
public sealed class DbConfigurationProvider : ConfigurationProvider
{
    private readonly string _connectionString;

    public DbConfigurationProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// No-op. <see cref="IConfigurationProvider"/> loads synchronously, but we never
    /// block on the database. Values are populated by <see cref="ReloadAsync"/> once
    /// the host has started (and migrations have created the table), and again after
    /// each settings write.
    /// </summary>
    public override void Load()
    {
    }

    /// <summary>Reads the table asynchronously, replaces the data and fires change tokens.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var options = new DbContextOptionsBuilder<SettingsDbContext>()
                .UseSqlite(_connectionString)
                .Options;

            await using var db = new SettingsDbContext(options);

            var entries = await db.UserSettings
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var entry in entries)
            {
                data[$"UserSettings:{entry.Key}"] = entry.Value;
            }
        }
        catch (SqliteException)
        {
            // Table may not exist yet (pre-migration). Start with empty data.
        }

        Data = data;
        OnReload();
    }
}
