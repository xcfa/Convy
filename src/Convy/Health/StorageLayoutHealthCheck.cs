using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Convy.Health;

/// <summary>
/// Reports the last storage layout check (rule paths vs. download directories). Problems
/// make the service <see cref="HealthStatus.Degraded"/>: Convy keeps running, but linking
/// into the affected paths will fail.
/// </summary>
public sealed class StorageLayoutHealthCheck : IHealthCheck
{
    private readonly StorageLayoutStatus _status;

    public StorageLayoutHealthCheck(StorageLayoutStatus status)
    {
        _status = status;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Evaluate(_status));
    }

    private static HealthCheckResult Evaluate(StorageLayoutStatus status)
    {
        if (status.Problems.Count > 0)
        {
            return HealthCheckResult.Degraded(string.Join(" ", status.Problems));
        }

        if (status.CheckedAt is null)
        {
            return HealthCheckResult.Healthy("Storage layout not checked yet.");
        }

        return status.Unchecked.Count > 0
            ? HealthCheckResult.Healthy($"Storage layout not checked yet for: {string.Join(", ", status.Unchecked)}.")
            : HealthCheckResult.Healthy("Rule paths share a filesystem with the download directories.");
    }
}
