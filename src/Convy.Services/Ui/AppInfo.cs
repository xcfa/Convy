using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Convy.Services.Ui;

/// <summary>What this Convy is and where it keeps its data.</summary>
public static class AppInfo
{
    private static readonly string Informational = ReadInformationalVersion();

    /// <summary>The full version, e.g. <c>2.2.0+9d6a257ffe6e…</c> (the SDK appends the commit).</summary>
    public static string InformationalVersion => Informational;

    /// <summary>The version with a short commit, e.g. <c>2.2.0+9d6a257</c>.</summary>
    public static string Version { get; } = Shorten(Informational);

    /// <summary>The commit the build was made from, when the build knew it.</summary>
    public static string? Commit { get; } = Informational.IndexOf('+') is var plus and >= 0 ? Informational[(plus + 1)..] : null;

    public static DateTimeOffset StartedAt { get; } = ReadStartTime();

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    public static string OperatingSystem => RuntimeInformation.OSDescription;

    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>The SQLite file of a connection string, or <c>null</c> for an in-memory database.</summary>
    public static string? DatabaseFile(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        return string.IsNullOrWhiteSpace(dataSource) || dataSource.StartsWith(':') ? null : Path.GetFullPath(dataSource);
    }

    /// <summary>Where the keys protecting the sign-in cookie are kept: <c>keys/</c> next to the database.</summary>
    public static string? KeysDirectory(string? connectionString) =>
        DatabaseFile(connectionString) is { } database && Path.GetDirectoryName(database) is { } directory
            ? Path.Combine(directory, "keys")
            : null;

    private static string ReadInformationalVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    /// <summary>A short hash is enough to tell builds apart.</summary>
    private static string Shorten(string version)
    {
        var plus = version.IndexOf('+');
        return plus < 0 || version.Length - plus <= 8 ? version : version[..(plus + 8)];
    }

    private static DateTimeOffset ReadStartTime()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }
}
