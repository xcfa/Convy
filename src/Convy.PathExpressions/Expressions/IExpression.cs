namespace Convy.PathExpressions.Expressions;

/// <summary>
/// A node in a compiled filter expression tree. Every node knows how to decide, for a
/// concrete item's property bag, whether it matches.
/// </summary>
public interface IExpression
{
    /// <summary>
    /// Evaluates this node against the given item properties (keyed by
    /// <see cref="Properties.RuleProperty.Name"/>).
    /// </summary>
    /// <remarks>
    /// A missing/<c>null</c> property is treated as "does not match" rather than
    /// throwing, so an incomplete property bag simply fails the comparison.
    /// </remarks>
    bool Evaluate(IReadOnlyDictionary<string, object?> properties);

    /// <summary>Adds the canonical names of every property this node references.</summary>
    void CollectProperties(ISet<string> names);
}
