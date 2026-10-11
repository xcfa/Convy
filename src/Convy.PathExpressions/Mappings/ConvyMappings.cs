using Convy.PathExpressions.Expressions;
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
/// top to bottom and the first match wins. An empty condition (<c>condition: ""</c>) matches
/// every item, e.g. for a catch-all rule at the end; leaving the key out is an error, so a
/// misspelt key never turns a rule into a catch-all.
/// </summary>
public sealed class ConvyMappings
{
    public IReadOnlyList<MappingRule> Rules { get; }

    private ConvyMappings(IReadOnlyList<MappingRule> rules) => Rules = rules;

    /// <summary>An empty rule set (matches nothing).</summary>
    public static ConvyMappings Empty { get; } = new([]);

    /// <summary>Returns the first rule that matches the item's properties, or <c>null</c>.</summary>
    public MappingRule? ResolveRule(IReadOnlyDictionary<string, object?> properties)
    {
        foreach (var rule in Rules)
        {
            if (rule.Matches(properties))
                return rule;
        }

        return null;
    }

    /// <summary>Returns the output path of the first rule that matches, or <c>null</c>.</summary>
    public string? Resolve(IReadOnlyDictionary<string, object?> properties) => ResolveRule(properties)?.OutputPath;

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

            // Keys are looked up by presence: "condition:" with no value is a catch-all, a
            // missing (or misspelt) key is an error.
            var fields = new Dictionary<string, string?>(entry ?? [], StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue("condition", out var condition))
                throw new FilterParseException(
                    $"Rule #{index}: 'condition' is required; use condition: \"\" for a rule that matches everything.");

            var path = fields.GetValueOrDefault("path");
            if (string.IsNullOrWhiteSpace(path))
                throw new FilterParseException($"Rule #{index}: 'path' is required.");

            var name = fields.GetValueOrDefault("name");
            var raw = condition?.Trim() ?? string.Empty;

            try
            {
                rules.Add(new MappingRule
                {
                    Condition = raw.Length == 0 ? TrueExpression.Instance : TorrentFilter.Parse(raw),
                    OutputPath = path.Trim(),
                    Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    RawCondition = raw,
                });
            }
            catch (FilterParseException ex)
            {
                throw new FilterParseException($"Rule #{index} ('{raw}'): {ex.Message}", ex);
            }
        }

        return new ConvyMappings(rules);
    }

    // YAML shape; populated by the deserializer. A rule is read as a map so that a key
    // given without a value can be told apart from a missing key.
    private sealed class RulesDocument
    {
        public List<Dictionary<string, string?>?>? Rules { get; set; }
    }
}
