using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Convy.Services.Ui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.KeyPerFile;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting;

namespace Convy.Configuration;

/// <summary>
/// Reads Convy's configuration for the About page: every value with the source that sets it
/// (the last source that has the key wins, as in the configuration itself).
/// </summary>
public sealed class ConfigurationInspector : IConfigurationInspector
{
    /// <summary>Where Docker secrets are read from (one file per setting).</summary>
    public const string SecretsDirectory = "/run/secrets";

    private readonly IConfigurationRoot _root;
    private readonly IHostEnvironment _environment;

    public ConfigurationInspector(IConfiguration configuration, IHostEnvironment environment)
    {
        _root = configuration as IConfigurationRoot
                ?? throw new ArgumentException("The configuration root is needed to tell the sources apart.", nameof(configuration));
        _environment = environment;
    }

    public string EnvironmentName => _environment.EnvironmentName;

    public string ContentRoot => _environment.ContentRootPath;

    public string? this[string key] => _root[key];

    public IReadOnlyList<ConfigurationSourceInfo> Sources => _root.Providers.Select(Describe).ToList();

    public IReadOnlyList<ConfigurationValue> Read(IReadOnlyCollection<string> sections)
    {
        var wanted = new HashSet<string>(sections, StringComparer.OrdinalIgnoreCase);
        var providers = _root.Providers.Reverse().ToList();

        return _root.AsEnumerable()
            .Where(kv => kv.Value is not null && wanted.Contains(kv.Key.Split(':')[0]))
            .Select(kv => new ConfigurationValue(kv.Key, kv.Value, SourceOf(kv.Key, providers)))
            .ToList();
    }

    /// <summary>
    /// "environment variables", or "environment variables (ASPNETCORE_*)" for the host's
    /// prefixed ones; the prefix is only exposed through the provider's description.
    /// </summary>
    private static string EnvironmentLabel(EnvironmentVariablesConfigurationProvider provider)
    {
        var description = provider.ToString();
        var start = description.IndexOf("Prefix: '", StringComparison.Ordinal);
        if (start < 0)
        {
            return "environment variables";
        }

        start += "Prefix: '".Length;
        var end = description.IndexOf('\'', start);
        var prefix = end > start ? description[start..end] : string.Empty;
        return prefix.Length == 0 ? "environment variables" : $"environment variables ({prefix}*)";
    }

    private static string SourceOf(string key, IReadOnlyList<IConfigurationProvider> providersLastFirst)
    {
        foreach (var provider in providersLastFirst)
        {
            if (provider.TryGet(key, out _))
            {
                return Describe(provider).Name;
            }
        }

        return "unknown";
    }

    private static ConfigurationSourceInfo Describe(IConfigurationProvider provider)
    {
        switch (provider)
        {
            case FileConfigurationProvider file:
            {
                var info = file.Source.FileProvider?.GetFileInfo(file.Source.Path ?? string.Empty);
                var path = info?.PhysicalPath ?? file.Source.Path;
                return new ConfigurationSourceInfo(file.Source.Path ?? "file", path, info?.Exists ?? false);
            }

            case KeyPerFileConfigurationProvider:
                return new ConfigurationSourceInfo("Docker secrets", SecretsDirectory, Directory.Exists(SecretsDirectory));

            case EnvironmentVariablesConfigurationProvider environment:
                return new ConfigurationSourceInfo(EnvironmentLabel(environment), null, null);

            case CommandLineConfigurationProvider:
                return new ConfigurationSourceInfo("command line", null, null);

            case DbConfigurationProvider:
                return new ConfigurationSourceInfo("UI settings (database)", null, null);

            case ChainedConfigurationProvider:
                return new ConfigurationSourceInfo("host settings", null, null);

            case MemoryConfigurationProvider:
                return new ConfigurationSourceInfo("built-in defaults", null, null);

            default:
                return new ConfigurationSourceInfo(provider.GetType().Name, null, null);
        }
    }
}
