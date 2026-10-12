using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Media;
using Microsoft.Extensions.Hosting;

namespace Convy.Services;

/// <summary>
/// Lists the search sources at start and again whenever the list is older than five minutes,
/// so that neither the UI nor a search waits for Prowlarr, slskd or an OPDS catalog.
/// </summary>
public sealed class SourceRefreshWorker : BackgroundService
{
    private readonly SourceRegistry _registry;

    public SourceRefreshWorker(SourceRegistry registry) => _registry = registry;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _registry.RunAsync(stoppingToken);
}
