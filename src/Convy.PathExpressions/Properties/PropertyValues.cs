using System.Globalization;

namespace Convy.PathExpressions.Properties;

/// <summary>
/// Reads and normalises values from an item's property bag. Producers should publish
/// numbers as <see cref="double"/>, but any numeric type, <see cref="TimeSpan"/> (seconds)
/// and <see cref="DateTimeOffset"/> (unix seconds) are accepted. A missing key, a
/// <c>null</c> value or a value of the wrong shape all read as <c>null</c>.
/// </summary>
public static class PropertyValues
{
    public static double? Number(IReadOnlyDictionary<string, object?> properties, string name) =>
        Get(properties, name) switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            decimal m => (double)m,
            TimeSpan t => t.TotalSeconds,
            DateTimeOffset o => o.ToUnixTimeSeconds(),
            short s => s,
            byte b => b,
            uint u => u,
            ulong ul => ul,
            _ => null,
        };

    public static string? Text(IReadOnlyDictionary<string, object?> properties, string name) =>
        Get(properties, name) switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };

    public static bool? Boolean(IReadOnlyDictionary<string, object?> properties, string name) =>
        Get(properties, name) as bool?;

    public static IEnumerable<string>? Collection(IReadOnlyDictionary<string, object?> properties, string name) =>
        Get(properties, name) as IEnumerable<string>;

    private static object? Get(IReadOnlyDictionary<string, object?> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value : null;
}
