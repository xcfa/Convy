using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Webhooks;

/// <summary>
/// The current webhook configuration. Reading it never throws: a version that cannot be
/// bound (e.g. an unknown <c>place</c>) is logged and the last good one stays in effect.
/// </summary>
public sealed class WebhookConfigSource
{
    private readonly IOptionsMonitor<List<WebhookConfig>> _monitor;
    private readonly ILogger<WebhookConfigSource> _logger;

    private volatile IReadOnlyList<WebhookConfig> _lastGood = [];
    private volatile string? _lastError;

    public WebhookConfigSource(IOptionsMonitor<List<WebhookConfig>> monitor, ILogger<WebhookConfigSource> logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    public IReadOnlyList<WebhookConfig> Current
    {
        get
        {
            try
            {
                var current = _monitor.CurrentValue;
                _lastGood = current;
                _lastError = null;
                return current;
            }
            catch (Exception ex)
            {
                if (_lastError != ex.Message)
                {
                    _lastError = ex.Message;
                    _logger.LogError(ex, "The webhooks configuration is invalid; the previous one stays in effect.");
                }

                return _lastGood;
            }
        }
    }
}
