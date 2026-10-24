using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using Convy.Sources;

namespace Convy.Services.Plugins;

/// <summary>
/// What an assembly of a package reaches past <see cref="IPluginContext"/>: host assemblies the package does not
/// carry, and framework APIs for the network, files, processes, native code, environment variables and loading code.
/// Read from metadata, without loading. A tripwire, not a sandbox: a plugin is still code in the server's process,
/// and reflection over a type it got legitimately can reach anything; the check catches a plugin that takes the
/// easy way around the host's HTTP (domain allow-list, rate limit, proxy) or reads Convy's secrets.
/// </summary>
internal static class PluginAudit
{
    /// <summary>Framework assemblies by name: the rest of the default context is the host's (EF Core, SQLite, ASP.NET Core, Convy).</summary>
    private static readonly string[] FrameworkPrefixes = ["System.", "Microsoft.Win32.", "Microsoft.VisualBasic", "Microsoft.CSharp"];

    private static readonly HashSet<string> FrameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "mscorlib", "netstandard", "WindowsBase",
    };

    /// <summary>Whole namespaces a plugin never needs.</summary>
    private static readonly HashSet<string> DeniedNamespaces = new(StringComparer.Ordinal)
    {
        "System.Net.Sockets", "System.Net.WebSockets", "System.Net.Quic", "System.Net.Security",
        "System.Net.NetworkInformation", "System.Net.Mail",
        "System.IO.Pipes", "System.IO.MemoryMappedFiles", "System.IO.IsolatedStorage", "System.IO.Enumeration",
        "System.Runtime.Loader", "System.Reflection.Emit", "System.Xml.Xsl",
        // Late binding (dynamic, VB's Option Strict Off) is invoking by reflection; VB's own file and network helpers.
        "Microsoft.CSharp.RuntimeBinder", "Microsoft.VisualBasic.FileIO", "Microsoft.VisualBasic.Devices",
        "Microsoft.VisualBasic.MyServices",
    };

    /// <summary>
    /// Types a plugin never needs. <c>System.Net.Http</c> itself is fine: <see cref="IPluginContext.Http"/> takes an
    /// <c>HttpRequestMessage</c> — only what sends one is not (and so <c>System.Net.Http.Json</c>: reading
    /// <c>HttpContent</c> is fine, its <c>HttpClient</c> extensions name <c>HttpClient</c>).
    /// </summary>
    private static readonly HashSet<string> DeniedTypes = new(StringComparer.Ordinal)
    {
        "System.Net.Http.HttpClient", "System.Net.Http.HttpClientHandler", "System.Net.Http.SocketsHttpHandler",
        "System.Net.Http.HttpMessageInvoker", "System.Net.Http.HttpMessageHandler", "System.Net.Http.DelegatingHandler",
        "System.Net.Http.WinHttpHandler",
        "System.Net.WebClient", "System.Net.WebRequest", "System.Net.HttpWebRequest", "System.Net.FtpWebRequest",
        "System.Net.FileWebRequest", "System.Net.Dns", "System.Net.HttpListener", "System.Net.ServicePointManager",
        "System.IO.File", "System.IO.FileInfo", "System.IO.Directory", "System.IO.DirectoryInfo",
        "System.IO.FileSystemInfo", "System.IO.FileStream", "System.IO.DriveInfo", "System.IO.FileSystemWatcher",
        "System.IO.RandomAccess", "System.IO.Compression.ZipFile", "System.IO.Compression.ZipFileExtensions",
        "System.Diagnostics.Process", "System.Diagnostics.ProcessStartInfo",
        "System.Runtime.InteropServices.Marshal", "System.Runtime.InteropServices.NativeLibrary",
        "System.Runtime.InteropServices.DllImportAttribute", "System.Runtime.InteropServices.LibraryImportAttribute",
        "System.AppDomain", "Microsoft.Win32.Registry", "Microsoft.Win32.RegistryKey",
        "System.Runtime.Remoting.ObjectHandle", // what Activator.CreateInstance(assemblyName, typeName) returns
        "System.Reflection.MethodInvoker", "System.Reflection.ConstructorInvoker", // invoke without MethodBase.Invoke
        "System.Xml.XmlUrlResolver",
        "Microsoft.VisualBasic.FileSystem", "Microsoft.VisualBasic.Interaction",
        "Microsoft.VisualBasic.CompilerServices.LateBinding", "Microsoft.VisualBasic.CompilerServices.NewLateBinding",
    };

    /// <summary>Members of otherwise harmless types: the environment, finding types by name, invoking by reflection.</summary>
    private static readonly Dictionary<string, HashSet<string>> DeniedMembers = new(StringComparer.Ordinal)
    {
        ["System.Environment"] =
        [
            "GetEnvironmentVariable", "GetEnvironmentVariables", "SetEnvironmentVariable", "Exit", "FailFast",
            "GetCommandLineArgs", "get_CommandLine", "get_CurrentDirectory", "set_CurrentDirectory",
            "ExpandEnvironmentVariables",
        ],
        ["System.Type"] = ["GetType", "InvokeMember", "GetTypeFromProgID", "GetTypeFromCLSID"],
        ["System.Reflection.Assembly"] =
        [
            "Load", "LoadFrom", "LoadFile", "UnsafeLoadFrom", "LoadWithPartialName", "GetType", "GetTypes",
            "GetExportedTypes", "get_ExportedTypes", "get_DefinedTypes", "GetForwardedTypes", "CreateInstance",
        ],
        ["System.Reflection.Module"] =
        [
            "GetType", "GetTypes", "FindTypes", "ResolveType", "ResolveMethod", "ResolveField", "ResolveMember",
        ],
        ["System.Reflection.MethodBase"] = ["Invoke"],
        ["System.Reflection.MethodInfo"] = ["Invoke", "CreateDelegate"],
        ["System.Delegate"] = ["CreateDelegate", "DynamicInvoke"],
        ["System.Linq.Expressions.LambdaExpression"] = ["Compile"],
        ["System.Linq.Expressions.Expression`1"] = ["Compile"],
        ["System.Reflection.ConstructorInfo"] = ["Invoke"],
        ["System.Reflection.PropertyInfo"] = ["GetValue", "SetValue"],
        ["System.Reflection.FieldInfo"] = ["GetValue", "SetValue", "GetValueDirect", "SetValueDirect"],
        ["System.Activator"] = ["CreateInstanceFrom"],
    };

    /// <summary>
    /// Readers and writers that open a file — or, for XML, any URL — when the first argument is a string (rather than a
    /// stream or a reader): constructors (<c>.ctor</c>) and methods by type.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> PathMembers = new(StringComparer.Ordinal)
    {
        ["System.IO.StreamReader"] = [".ctor"],
        ["System.IO.StreamWriter"] = [".ctor"],
        ["System.Xml.XmlReader"] = ["Create"],
        ["System.Xml.XmlWriter"] = ["Create"],
        ["System.Xml.XmlTextReader"] = [".ctor"],
        ["System.Xml.XmlTextWriter"] = [".ctor"],
        ["System.Xml.XmlDocument"] = ["Load", "Save"],
        ["System.Xml.XPath.XPathDocument"] = [".ctor"],
        ["System.Xml.Linq.XDocument"] = ["Load", "Save"],
        ["System.Xml.Linq.XElement"] = ["Load", "Save"],
        ["System.Xml.Linq.XStreamingElement"] = ["Save"],
        ["System.Security.Cryptography.X509Certificates.X509Certificate2"] = [".ctor"],
        ["System.Security.Cryptography.X509Certificates.X509Certificate"] = [".ctor", "CreateFromCertFile", "CreateFromSignedFile"],
        ["System.Diagnostics.FileVersionInfo"] = ["GetVersionInfo"],
    };

    /// <summary>
    /// What the assembly reaches past the host, as lines like <c>System.Net.Http.HttpClient</c>; empty — nothing.
    /// </summary>
    /// <param name="reader">The assembly's metadata.</param>
    /// <param name="package">The package's assemblies by simple name: references to them are the package's own.</param>
    /// <param name="forwarded">
    /// <see cref="ForwardedTypes"/> of the package: a reference to one of them through a package assembly (or the
    /// assembly's own exported types) is to the framework type it forwards to.
    /// </param>
    public static IReadOnlyList<string> Findings(
        MetadataReader reader, IReadOnlyDictionary<string, byte[]> package, IReadOnlySet<string> forwarded)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handle in reader.AssemblyReferences)
        {
            var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
            if (!IsFramework(name) && !package.ContainsKey(name) && name != SourcesApi.AssemblyName)
                findings.Add($"the assembly {name}, which the package does not carry (Convy's own copy)");
        }

        foreach (var handle in reader.TypeReferences)
        {
            if (FullName(reader, handle, package, forwarded) is { } type
                && (DeniedTypes.Contains(type) || DeniedNamespaces.Contains(NamespaceOf(type))))
                findings.Add(type);
        }

        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);
            if (ParentType(reader, member.Parent) is not { } parent
                || FullName(reader, parent, package, forwarded) is not { } type)
                continue;
            var name = reader.GetString(member.Name);
            if (DeniedMembers.TryGetValue(type, out var members) && members.Contains(name))
                findings.Add($"{type}.{name}");
            else if (PathMembers.TryGetValue(type, out var opening) && opening.Contains(name) && TakesStringFirst(reader, member))
                findings.Add(name == ".ctor" ? $"{type}(string path)" : $"{type}.{name}(string path)");
        }

        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                findings.Add($"native code (P/Invoke {reader.GetString(method.Name)})");
        }
        return [.. findings];
    }

    /// <summary>
    /// The types the package's assemblies forward out of the package (<c>[TypeForwardedTo]</c>), by full name: a facade
    /// that sends <c>System.Environment</c> on to the framework does not make it the package's own.
    /// </summary>
    public static IReadOnlySet<string> ForwardedTypes(IEnumerable<MetadataReader> readers, IReadOnlyDictionary<string, byte[]> package)
    {
        var forwarded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reader in readers)
        {
            foreach (var handle in reader.ExportedTypes)
            {
                var exported = reader.GetExportedType(handle);
                if (exported.Implementation.Kind != HandleKind.AssemblyReference)
                    continue; // nested (named by the outer one) or in another module of the same assembly
                var target = reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation);
                if (!package.ContainsKey(reader.GetString(target.Name)))
                    forwarded.Add(Join(reader.GetString(exported.Namespace), reader.GetString(exported.Name)));
            }
        }
        return forwarded;
    }

    private static bool IsFramework(string name) =>
        FrameworkNames.Contains(name) || FrameworkPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string NamespaceOf(string type) => type[..Math.Max(0, type.LastIndexOf('.'))];

    private static string Join(string ns, string name) => ns.Length == 0 ? name : $"{ns}.{name}";

    /// <summary>
    /// The type a member reference is on: a type reference, or the generic type of an instantiation
    /// (<c>Expression&lt;T&gt;.Compile</c>); <c>null</c> — a definition of the assembly itself, an array and the like.
    /// </summary>
    private static TypeReferenceHandle? ParentType(MetadataReader reader, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeReference)
            return (TypeReferenceHandle)parent;
        if (parent.Kind != HandleKind.TypeSpecification)
            return null;
        var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            return null;
        blob.ReadSignatureTypeCode(); // class or value type
        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)generic : null;
    }

    /// <summary>
    /// The full name of a referenced type outside the package (a nested one is named by its outermost type);
    /// <c>null</c> — the type is the package's own.
    /// </summary>
    private static string? FullName(
        MetadataReader reader, TypeReferenceHandle handle, IReadOnlyDictionary<string, byte[]> package, IReadOnlySet<string> forwarded)
    {
        var type = reader.GetTypeReference(handle);
        while (type.ResolutionScope.Kind == HandleKind.TypeReference)
            type = reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
        var name = Join(reader.GetString(type.Namespace), reader.GetString(type.Name));
        if (type.ResolutionScope.Kind == HandleKind.AssemblyReference
            && !package.ContainsKey(reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name)))
            return name;
        // A package assembly, or no scope — the assembly's own exported types: its own type unless forwarded on.
        return forwarded.Contains(name) ? name : null;
    }

    /// <summary>Whether a method's or constructor's first parameter is a <see cref="string"/>.</summary>
    private static bool TakesStringFirst(MetadataReader reader, MemberReference member)
    {
        if (member.GetKind() != MemberReferenceKind.Method)
            return false;
        var signature = member.DecodeMethodSignature(IsString.Provider, null);
        return signature.ParameterTypes is [true, ..];
    }

    /// <summary>Decodes a signature's types as "is it <see cref="string"/>".</summary>
    private sealed class IsString : ISignatureTypeProvider<bool, object?>
    {
        public static readonly IsString Provider = new();

        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode == PrimitiveTypeCode.String;
        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => unmodifiedType;
        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => false;
        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => false;
        public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => false;
        public bool GetSZArrayType(bool elementType) => false;
        public bool GetArrayType(bool elementType, ArrayShape shape) => false;
        public bool GetByReferenceType(bool elementType) => false;
        public bool GetPointerType(bool elementType) => false;
        public bool GetPinnedType(bool elementType) => false;
        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) => false;
        public bool GetGenericMethodParameter(object? genericContext, int index) => false;
        public bool GetGenericTypeParameter(object? genericContext, int index) => false;
        public bool GetFunctionPointerType(MethodSignature<bool> signature) => false;
    }
}
