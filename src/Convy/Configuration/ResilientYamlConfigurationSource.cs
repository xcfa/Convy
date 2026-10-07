using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using NetEscapades.Configuration.Yaml;
using Serilog;

namespace Convy.Configuration;

/// <summary>
/// YAML configuration file that survives broken edits: when a reload fails to parse, the
/// previous configuration stays in effect (and the error is logged) instead of the file's
/// settings disappearing. A broken file at startup still fails fast.
/// </summary>
public sealed class ResilientYamlConfigurationSource : YamlConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new ResilientYamlConfigurationProvider(this);
    }
}

/// <inheritdoc cref="ResilientYamlConfigurationSource"/>
public sealed class ResilientYamlConfigurationProvider : YamlConfigurationProvider
{
    private bool _loaded;

    public ResilientYamlConfigurationProvider(YamlConfigurationSource source)
        : base(source)
    {
    }

    public override void Load(Stream stream)
    {
        try
        {
            // The base parser only replaces Data after a successful parse.
            base.Load(stream);
            _loaded = true;
        }
        catch (Exception ex) when (_loaded)
        {
            Log.Error(ex, "Could not parse '{Path}'; the previous configuration stays in effect.", Source.Path);
        }
    }
}
