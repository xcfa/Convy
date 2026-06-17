using Banned.Qbittorrent.Models.Torrent;
using Convy.PathExpressions.Parsing;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Convy.PathExpressions.Mappings;

/// <summary>
/// An ordered set of <see cref="MappingRule"/>s loaded from a YAML rules file:
/// <code>
/// rules:
///   - condition: "Size > 100 &amp;&amp; Tags.Contains(Test) &amp;&amp; Category == Test"
///     path: /data/media/test
///   - condition: "State == StalledUpload || Ratio >= 2.0"
///     path: /data/media/done
/// </code>
/// The condition is the filter DSL; <c>path</c> is the destination. Rules are evaluated
/// top to bottom and the first match wins.
/// </summary>
public sealed class ConvyMappings
{
    public IReadOnlyList<MappingRule> Rules { get; }

    private ConvyMappings(IReadOnlyList<MappingRule> rules) => Rules = rules;

    /// <summary>An empty rule set (matches nothing).</summary>
    public static ConvyMappings Empty { get; } = new([]);

    /// <summary>Returns the output path of the first rule that matches, or <c>null</c>.</summary>
    public string? Resolve(TorrentInfo info)
    {
        foreach (var rule in Rules)
        {
            if (rule.Matches(info))
                return rule.OutputPath;
        }

        return null;
    }

    public static ConvyMappings LoadFromFile(string path) => ParseYaml(File.ReadAllText(path));

    public static ConvyMappings ParseYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        RulesDocument? document;
        try
        {
            document = deserializer.Deserialize<RulesDocument>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FilterParseException($"Invalid YAML: {ex.Message}", ex);
        }

        if (document?.Rules is not { Count: > 0 } entries)
            return Empty;

        var rules = new List<MappingRule>(entries.Count);
        var index = 0;

        foreach (var entry in entries)
        {
            index++;

            if (string.IsNullOrWhiteSpace(entry.Condition))
                throw new FilterParseException($"Rule #{index}: 'condition' is required.");
            if (string.IsNullOrWhiteSpace(entry.Path))
                throw new FilterParseException($"Rule #{index}: 'path' is required.");

            try
            {
                rules.Add(new MappingRule
                {
                    Condition = TorrentFilter.Parse(entry.Condition),
                    OutputPath = entry.Path.Trim(),
                    RawCondition = entry.Condition.Trim(),
                });
            }
            catch (FilterParseException ex)
            {
                throw new FilterParseException($"Rule #{index} ('{entry.Condition.Trim()}'): {ex.Message}", ex);
            }
        }

        return new ConvyMappings(rules);
    }

    // YAML shapes; populated by the deserializer.
    private sealed class RulesDocument
    {
        public List<RuleEntry>? Rules { get; set; }
    }

    private sealed class RuleEntry
    {
        public string? Condition { get; set; }
        public string? Path { get; set; }
    }
}
