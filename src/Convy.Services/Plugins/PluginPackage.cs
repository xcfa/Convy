using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Convy.Sources;

namespace Convy.Services.Plugins;

/// <summary>A plugin package cannot be used: not a .NET assembly, no plugin in it, or it reaches past the host.</summary>
public sealed class PluginPackageException : Exception
{
    public PluginPackageException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A plugin package read without loading it: a single DLL, or a zip whose top-level DLLs are the plugin and its own
/// dependencies. The main assembly is the one that references <c>Convy.Sources.Abstractions</c>; its name is the
/// package, its version the package's version, and the version of that reference the API version it was built against.
/// </summary>
public sealed partial class PluginPackage
{
    /// <summary>Largest unpacked zip: a guard against zip bombs.</summary>
    private const long MaxUnpackedBytes = 128L * 1024 * 1024;

    private PluginPackage(string package, Version version, Version apiVersion, IReadOnlyDictionary<string, byte[]> assemblies, string sha256)
    {
        Package = package;
        Version = version;
        ApiVersion = apiVersion;
        Assemblies = assemblies;
        Sha256 = sha256;
    }

    /// <summary>The main assembly's simple name: <c>Convy.Plugins.EHentai</c>.</summary>
    public string Package { get; }

    public Version Version { get; }

    /// <summary>The <c>Convy.Sources.Abstractions</c> version the main assembly references.</summary>
    public Version ApiVersion { get; }

    /// <summary>The package's assemblies by simple name, the main one included.</summary>
    public IReadOnlyDictionary<string, byte[]> Assemblies { get; }

    /// <summary>SHA-256 of the package as given, lowercase hex.</summary>
    public string Sha256 { get; }

    /// <summary>The version as shown: <c>1.2.0</c>.</summary>
    public string VersionText => Version.Revision > 0 ? Version.ToString(4) : Version.ToString(3);

    /// <summary>
    /// Whether a plugin built against <see cref="ApiVersion"/> can run on this host: the same major, a minor up to the
    /// host's (<see cref="SourcesApi.Version"/>).
    /// </summary>
    public bool IsCompatible =>
        ApiVersion.Major == SourcesApi.Version.Major && ApiVersion.Minor <= SourcesApi.Version.Minor;

    /// <param name="content">The DLL or the zip.</param>
    /// <param name="trusted">
    /// Skip the audit: the plugin may use what <see cref="PluginAudit"/> would otherwise refuse (the owner built it).
    /// </param>
    /// <exception cref="PluginPackageException">Not a DLL or zip of DLLs, no main assembly, several; untrusted and reaching past the host.</exception>
    public static PluginPackage Read(byte[] content, bool trusted)
    {
        ArgumentNullException.ThrowIfNull(content);
        var files = IsZip(content) ? Unzip(content) : new Dictionary<string, byte[]> { [""] = content };

        var assemblies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var mains = new List<(string Name, Version Version, Version Api)>();
        foreach (var (file, bytes) in files)
        {
            var (name, version, api) = Inspect(bytes, file);
            if (!assemblies.TryAdd(name, bytes))
            {
                throw Invalid($"The package has the assembly {name} twice.");
            }

            if (api is not null)
            {
                mains.Add((name, version, api));
            }
        }

        if (mains.Count == 0)
        {
            throw Invalid($"No assembly of the package references {SourcesApi.AssemblyName}: it is not a Convy plugin.");
        }

        if (mains.Count > 1)
        {
            throw Invalid($"Several assemblies of the package reference {SourcesApi.AssemblyName} ({string.Join(", ", mains.Select(m => m.Name))}); a package is one plugin.");
        }

        var main = mains[0];
        if (!PackageName().IsMatch(main.Name))
        {
            throw Invalid($"The assembly name {main.Name} cannot be a package name (latin letters, digits, '.', '_', '-').");
        }

        if (assemblies.ContainsKey(SourcesApi.AssemblyName))
        {
            throw Invalid($"The package carries its own {SourcesApi.AssemblyName}; Convy provides it.");
        }

        if (!trusted)
        {
            Audit(assemblies);
        }

        return new PluginPackage(main.Name, main.Version, main.Api, assemblies, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    private static bool IsZip(byte[] content) =>
        content.Length >= 4 && content[0] == 'P' && content[1] == 'K' && content[2] == 3 && content[3] == 4;

    private static Dictionary<string, byte[]> Unzip(byte[] content)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                // Top-level DLLs only: the rest (pdb, readme, folders) is not loaded.
                if (entry.FullName.Contains('/') || entry.FullName.Contains('\\')
                    || !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                total += entry.Length;
                if (total > MaxUnpackedBytes)
                {
                    throw Invalid($"The package unpacks to more than {MaxUnpackedBytes / (1024 * 1024)} MB.");
                }

                using var stream = entry.Open();
                using var copy = new MemoryStream((int)entry.Length);
                stream.CopyTo(copy);
                files[entry.Name] = copy.ToArray();
            }
        }
        catch (InvalidDataException ex)
        {
            throw Invalid($"The zip cannot be read: {ex.Message}");
        }

        if (files.Count == 0)
        {
            throw Invalid("The zip has no DLL at its top level.");
        }

        return files;
    }

    /// <summary>The assembly's name and version, and the version of its reference to the API (<c>null</c> — none).</summary>
    private static (string Name, Version Version, Version? Api) Inspect(byte[] bytes, string file)
    {
        var what = file.Length == 0 ? "The file" : file;
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes));
            if (!pe.HasMetadata)
            {
                throw Invalid($"{what} is not a .NET assembly.");
            }

            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                throw Invalid($"{what} is a module, not an assembly.");
            }

            var definition = reader.GetAssemblyDefinition();
            var name = reader.GetString(definition.Name);
            Version? api = null;
            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                if (reader.StringComparer.Equals(reference.Name, SourcesApi.AssemblyName))
                {
                    api = reference.Version;
                }
            }

            return (name, definition.Version, api);
        }
        catch (BadImageFormatException)
        {
            throw Invalid($"{what} is not a .NET assembly.");
        }
    }

    /// <exception cref="PluginPackageException">Listing what the package reaches past the host.</exception>
    private static void Audit(Dictionary<string, byte[]> assemblies)
    {
        const int shown = 8;
        var readers = assemblies.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => (a.Key, Pe: new PEReader(new MemoryStream(a.Value))))
            .ToList();
        var findings = new List<string>();
        try
        {
            var forwarded = PluginAudit.ForwardedTypes(readers.Select(r => r.Pe.GetMetadataReader()), assemblies);
            foreach (var (name, pe) in readers)
            {
                var found = PluginAudit.Findings(pe.GetMetadataReader(), assemblies, forwarded);
                if (found.Count > 0)
                {
                    findings.Add($"{name}: {string.Join(", ", found.Take(shown))}{(found.Count > shown ? $" and {found.Count - shown} more" : "")}");
                }
            }
        }
        finally
        {
            foreach (var (_, pe) in readers)
            {
                pe.Dispose();
            }
        }

        if (findings.Count == 0)
        {
            return;
        }

        throw Invalid($"The package goes around Convy's plugin API ({string.Join("; ", findings)}). Set plugins.trusted: true to load it anyway.");
    }

    private static PluginPackageException Invalid(string message) => new(message);

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$")]
    private static partial Regex PackageName();
}
