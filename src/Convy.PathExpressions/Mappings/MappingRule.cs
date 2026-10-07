using Convy.PathExpressions.Expressions;

namespace Convy.PathExpressions.Mappings;

/// <summary>
/// One routing rule: a compiled <see cref="Condition"/> plus the <see cref="OutputPath"/>
/// an item is routed to when the condition holds.
/// </summary>
public sealed class MappingRule
{
    private IReadOnlySet<string>? _referencedProperties;

    public required IExpression Condition { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>
    /// Optional rule name. Used to scope webhooks to specific rules; <c>null</c>
    /// when the rule is unnamed.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>The original condition text, kept for logging / diagnostics.</summary>
    public required string RawCondition { get; init; }

    /// <summary>Canonical names of every property the condition references.</summary>
    public IReadOnlySet<string> ReferencedProperties => _referencedProperties ??= CollectReferencedProperties();

    /// <summary>
    /// Evaluates the condition against an item's property bag. When the item lacks any
    /// property the condition references (e.g. <c>Ratio</c> or <c>Tags</c> on a Soulseek
    /// download), the rule is skipped as a whole and does not match: judging a single
    /// comparison false instead would make <c>!Tags.Contains(skip)</c> true for such items.
    /// A property that is present but <c>null</c> still takes part in the evaluation.
    /// </summary>
    public bool Matches(IReadOnlyDictionary<string, object?> properties)
    {
        foreach (var name in ReferencedProperties)
        {
            if (!properties.ContainsKey(name))
                return false;
        }

        return Condition.Evaluate(properties);
    }

    private HashSet<string> CollectReferencedProperties()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Condition.CollectProperties(names);
        return names;
    }
}
