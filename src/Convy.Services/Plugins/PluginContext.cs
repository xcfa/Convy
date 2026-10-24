using Convy.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Convy.Services.Plugins;

/// <summary>The host side of one plugin: its HTTP, its live settings and a log under its id.</summary>
public sealed class PluginContext : IPluginContext, IPluginSettings, IPluginLog, IDisposable
{
    private readonly string _pluginId;
    private readonly IOptionsMonitor<PluginsOptions> _options;
    private readonly PluginHttp _http;
    private readonly ILogger _logger;

    public PluginContext(PluginManifest manifest, IOptionsMonitor<PluginsOptions> options, HttpMessageHandler handler, TimeProvider timeProvider, ILogger logger)
    {
        _pluginId = manifest.Id;
        _options = options;
        _logger = logger;
        _http = new PluginHttp(manifest, handler, () => TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.For(manifest.Id).TimeoutSeconds)), timeProvider);
    }

    public IPluginHttp Http => _http;

    public IPluginSettings Settings => this;

    public IPluginLog Log => this;

    /// <summary>Whether the plugin is switched on in the current configuration.</summary>
    public bool Enabled => _options.CurrentValue.For(_pluginId).Enabled;

    string? IPluginSettings.Get(string key)
    {
        var value = _options.CurrentValue.For(_pluginId).Values.GetValueOrDefault(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    void IPluginLog.Write(PluginLogLevel level, string message, Exception? exception) =>
        _logger.Log(level switch
        {
            PluginLogLevel.Debug => LogLevel.Debug,
            PluginLogLevel.Warning => LogLevel.Warning,
            PluginLogLevel.Error => LogLevel.Error,
            _ => LogLevel.Information,
        }, exception, "[{Plugin}] {Message}", _pluginId, message);

    public void Dispose() => _http.Dispose();
}
