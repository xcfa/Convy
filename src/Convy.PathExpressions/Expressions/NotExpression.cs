namespace Convy.PathExpressions.Expressions;

/// <summary>Logical negation (<c>!</c>).</summary>
public sealed class NotExpression : IExpression
{
    private readonly IExpression _operand;

    public NotExpression(IExpression operand) => _operand = operand;

    public bool Evaluate(IReadOnlyDictionary<string, object?> properties) => !_operand.Evaluate(properties);

    public void CollectProperties(ISet<string> names) => _operand.CollectProperties(names);

    public override string ToString() => $"!{_operand}";
}
