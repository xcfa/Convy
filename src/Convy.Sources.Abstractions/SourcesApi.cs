namespace Convy.Sources;

/// <summary>The version of this API, as a plugin was built against it and as the host offers it.</summary>
public static class SourcesApi
{
    /// <summary>The simple name of this assembly: the one a plugin's main assembly references.</summary>
    public const string AssemblyName = "Convy.Sources.Abstractions";

    /// <summary>
    /// Major.minor of the API. The host loads plugins built against the same major and a minor up to this one.
    /// Keep in step with the csproj's <c>AssemblyVersion</c>.
    /// </summary>
    public static readonly Version Version = new(1, 0);
}
