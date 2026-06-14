using System.Globalization;
using Antlr4.Runtime.Tree;
using Convy.PathExpressions.Expressions;
using Convy.PathExpressions.Grammar;
using Convy.PathExpressions.Properties;

namespace Convy.PathExpressions.Parsing;

/// <summary>
/// Walks the ANTLR parse tree and produces a typed <see cref="IExpression"/> tree.
/// All semantic validation (unknown property, illegal operator for a type,
/// non-numeric literal, …) happens here and is reported as
/// <see cref="FilterParseException"/>.
/// </summary>
internal sealed class FilterExpressionBuilder : TorrentFilterBaseVisitor<IExpression>
{
    public override IExpression VisitFilter(TorrentFilterParser.FilterContext context) =>
        Visit(context.expression());

    public override IExpression VisitParenExpression(TorrentFilterParser.ParenExpressionContext context) =>
        Visit(context.expression());

    public override IExpression VisitNotExpression(TorrentFilterParser.NotExpressionContext context) =>
        new NotExpression(Visit(context.expression()));

    public override IExpression VisitAndExpression(TorrentFilterParser.AndExpressionContext context) =>
        new AndExpression(Visit(context.expression(0)), Visit(context.expression(1)));

    public override IExpression VisitOrExpression(TorrentFilterParser.OrExpressionContext context) =>
        new OrExpression(Visit(context.expression(0)), Visit(context.expression(1)));

    public override IExpression VisitPredicateExpression(TorrentFilterParser.PredicateExpressionContext context) =>
        Visit(context.predicate());

    public override IExpression VisitComparisonPredicate(TorrentFilterParser.ComparisonPredicateContext context)
    {
        var name = context.property().GetText();
        var prop = Resolve(name);
        var op = ParseOperator(context.comparator().GetText());
        var literal = ReadLiteral(context.value());

        return prop.Kind switch
        {
            PropertyKind.Numeric => new NumericCompareExpression(
                prop.Name, prop.Number!, op, ParseNumber(prop.Name, literal)),

            PropertyKind.Text => new StringCompareExpression(
                prop.Name, prop.Text!, RequireEquality(prop, op), literal.AsString()),

            PropertyKind.Boolean => new BoolCompareExpression(
                prop.Name, prop.Boolean!, RequireEquality(prop, op), ParseBool(prop.Name, literal)),

            PropertyKind.Collection => throw new FilterParseException(
                $"Property '{prop.Name}' is a list and cannot be compared with '{op.ToSymbol()}'. " +
                $"Use {prop.Name}.Contains(value) instead."),

            _ => throw new FilterParseException($"Unsupported property kind for '{prop.Name}'."),
        };
    }

    public override IExpression VisitContainsPredicate(TorrentFilterParser.ContainsPredicateContext context)
    {
        var name = context.property().GetText();
        var prop = Resolve(name);

        if (prop.Kind != PropertyKind.Collection)
            throw new FilterParseException(
                $"'.Contains(...)' is only valid on list properties; '{prop.Name}' is {prop.Kind}.");

        return new ContainsExpression(prop.Name, prop.Collection!, ReadLiteral(context.value()).AsString());
    }

    // ---------------------------------------------------------------- helpers

    private static TorrentProperty Resolve(string name) =>
        TorrentInfoProperties.Find(name)
        ?? throw new FilterParseException($"Unknown property '{name}'.");

    private static ComparisonOperator RequireEquality(TorrentProperty prop, ComparisonOperator op)
    {
        if (!op.IsEquality())
            throw new FilterParseException(
                $"Operator '{op.ToSymbol()}' is not valid for the {prop.Kind.ToString().ToLowerInvariant()} " +
                $"property '{prop.Name}'; only == and != are allowed.");
        return op;
    }

    private static ComparisonOperator ParseOperator(string symbol) => symbol switch
    {
        "==" => ComparisonOperator.Equal,
        "!=" => ComparisonOperator.NotEqual,
        ">"  => ComparisonOperator.Greater,
        ">=" => ComparisonOperator.GreaterOrEqual,
        "<"  => ComparisonOperator.Less,
        "<=" => ComparisonOperator.LessOrEqual,
        _    => throw new FilterParseException($"Unknown operator '{symbol}'."),
    };

    private static double ParseNumber(string propertyName, LiteralToken literal)
    {
        if (literal.Kind != LiteralKind.Number ||
            !double.TryParse(literal.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new FilterParseException(
                $"Property '{propertyName}' expects a numeric value but got '{literal.Text}'.");
        }

        return value;
    }

    private static bool ParseBool(string propertyName, LiteralToken literal)
    {
        if (literal.Kind == LiteralKind.Bool && bool.TryParse(literal.Text, out var value))
            return value;

        throw new FilterParseException(
            $"Property '{propertyName}' expects 'true' or 'false' but got '{literal.Text}'.");
    }

    private static LiteralToken ReadLiteral(TorrentFilterParser.ValueContext value)
    {
        if (value.BOOL() is { } b)
            return new LiteralToken(LiteralKind.Bool, b.GetText());
        if (value.NUMBER() is { } n)
            return new LiteralToken(LiteralKind.Number, n.GetText());
        if (value.STRING() is { } s)
            return new LiteralToken(LiteralKind.String, Unescape(s.GetText()));
        if (value.IDENTIFIER() is { } id)
            return new LiteralToken(LiteralKind.Word, id.GetText());

        throw new FilterParseException($"Unrecognised value '{value.GetText()}'.");
    }

    /// <summary>Strips the surrounding quotes and resolves <c>\"</c> / <c>\\</c> escapes.</summary>
    private static string Unescape(string quoted)
    {
        // quoted always includes the leading and trailing double quote.
        var inner = quoted.Substring(1, quoted.Length - 2);
        if (!inner.Contains('\\'))
            return inner;

        var sb = new System.Text.StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '\\' && i + 1 < inner.Length)
            {
                i++;
                sb.Append(inner[i]);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private enum LiteralKind { Bool, Number, String, Word }

    private readonly record struct LiteralToken(LiteralKind Kind, string Text)
    {
        /// <summary>The literal as a plain string (barewords and quoted strings alike).</summary>
        public string AsString() => Text;
    }
}
