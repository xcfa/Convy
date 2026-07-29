using System;
using System.Threading;
using System.Threading.Tasks;
using Convy.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Convy.Health;

/// <summary>
/// Reports healthy when the application's SQLite database is reachable.
/// Both <see cref="ConvyDbContext"/> and the settings context share the same
/// file, so a single connectivity probe covers the whole database.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<ConvyDbContext> _dbFactory;

    public DatabaseHealthCheck(IDbContextFactory<ConvyDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy("Database is reachable.")
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connectivity check failed.", ex);
        }
    }
}
