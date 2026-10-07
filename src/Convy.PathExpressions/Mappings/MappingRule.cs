using Convy.PathExpressions.Expressions;

namespace Convy.PathExpressions.Mappings;

/// <summary>
/// One routing rule: a compiled <see cref="Condition"/> plus the <see cref="OutputPath"/>
/// an item is routed to when the condition holds.
/// </summary>
public sealed class MappingRule
{
    public required IExpression Condition { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>
    /// Optional rule name. Used to scope webhooks to specific rules; <c>null</c>
    /// when the rule is unnamed.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>The original condition text, kept for logging / diagnostics.</summary>
    public required string RawCondition { get; init; }

    /// <summary>Evaluates the condition against an item's property bag.</summary>
    public bool Matches(IReadOnlyDictionary<string, object?> properties) => Condition.Evaluate(properties);
}
