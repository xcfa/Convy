namespace Convy.Sources.Abstractions;

/// <summary>The version of this API, as an extension references it.</summary>
public static class ConvySourcesApi
{
    /// <summary>The API version of this assembly: the same as its <c>AssemblyVersion</c> (major.minor).</summary>
    public static Version Version { get; } = new(2, 0);

    /// <summary>The name of this assembly, as an extension references it.</summary>
    public const string AssemblyName = "Convy.Sources.Abstractions";
}
