namespace Convy.PathExpressions.Expressions;

/// <summary>
/// The condition of a rule written with an empty <c>condition</c>: matches every item and
/// references no property, so it also matches items that lack properties other rules use.
/// </summary>
public sealed class TrueExpression : IExpression
{
    public static TrueExpression Instance { get; } = new();

    private TrueExpression()
    {
    }

    public bool Evaluate(IReadOnlyDictionary<string, object?> properties) => true;

    public void CollectProperties(ISet<string> names)
    {
    }

    public override string ToString() => "true";
}
