namespace Convy.PathExpressions.Expressions;

/// <summary>Logical conjunction (<c>&amp;&amp;</c>). Short-circuits on the left operand.</summary>
public sealed class AndExpression : IExpression
{
    private readonly IExpression _left;
    private readonly IExpression _right;

    public AndExpression(IExpression left, IExpression right)
    {
        _left = left;
        _right = right;
    }

    public bool Evaluate(IReadOnlyDictionary<string, object?> properties) =>
        _left.Evaluate(properties) && _right.Evaluate(properties);

    public void CollectProperties(ISet<string> names)
    {
        _left.CollectProperties(names);
        _right.CollectProperties(names);
    }

    public override string ToString() => $"({_left} && {_right})";
}
