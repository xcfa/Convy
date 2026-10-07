using System.Threading;
using System.Threading.Tasks;
using Convy.Services.Webhooks;
using Microsoft.Extensions.Hosting;

namespace Convy.Services;

/// <summary>Runs the webhook event dispatcher for the lifetime of the host.</summary>
public sealed class WebhookDispatchWorker : BackgroundService
{
    private readonly WebhookEventDispatcher _dispatcher;

    public WebhookDispatchWorker(WebhookEventDispatcher dispatcher) => _dispatcher = dispatcher;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _dispatcher.RunAsync(stoppingToken);
}
