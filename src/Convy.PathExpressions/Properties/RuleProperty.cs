namespace Convy.PathExpressions.Properties;

/// <summary>
/// Describes one property the filter DSL may reference: its canonical name and the
/// <see cref="PropertyKind"/> that decides which operators and literals are legal.
/// Values are read at evaluation time from the item's property bag by <see cref="Name"/>.
/// </summary>
public sealed class RuleProperty
{
    public required string Name { get; init; }
    public required PropertyKind Kind { get; init; }
}
