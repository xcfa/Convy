namespace Convy.PathExpressions.Expressions;

/// <summary>The relational operators supported by the filter DSL.</summary>
public enum ComparisonOperator
{
    Equal,
    NotEqual,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
}

public static class ComparisonOperatorExtensions
{
    /// <summary>
    /// Applies the operator to the result of an <see cref="IComparable.CompareTo"/>
    /// call (negative / zero / positive).
    /// </summary>
    public static bool Apply(this ComparisonOperator op, int comparison) => op switch
    {
        ComparisonOperator.Equal          => comparison == 0,
        ComparisonOperator.NotEqual       => comparison != 0,
        ComparisonOperator.Greater        => comparison > 0,
        ComparisonOperator.GreaterOrEqual => comparison >= 0,
        ComparisonOperator.Less           => comparison < 0,
        ComparisonOperator.LessOrEqual    => comparison <= 0,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown operator."),
    };

    /// <summary>True for the equality operators (<c>==</c> / <c>!=</c>).</summary>
    public static bool IsEquality(this ComparisonOperator op) =>
        op is ComparisonOperator.Equal or ComparisonOperator.NotEqual;

    /// <summary>Renders the operator back to its DSL spelling (for error messages).</summary>
    public static string ToSymbol(this ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal          => "==",
        ComparisonOperator.NotEqual       => "!=",
        ComparisonOperator.Greater        => ">",
        ComparisonOperator.GreaterOrEqual => ">=",
        ComparisonOperator.Less           => "<",
        ComparisonOperator.LessOrEqual    => "<=",
        _ => op.ToString(),
    };
}
