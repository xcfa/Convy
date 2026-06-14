using Antlr4.Runtime;
using Convy.PathExpressions.Expressions;
using Convy.PathExpressions.Grammar;

namespace Convy.PathExpressions.Parsing;

/// <summary>
/// Entry point for the filter DSL: compiles a textual condition such as
/// <c>Size &gt; 100 &amp;&amp; Uploaded == 0 &amp;&amp; Tags.Contains(Test) &amp;&amp; Category == Test</c>
/// into an <see cref="IExpression"/> that can be evaluated against a torrent.
/// </summary>
public static class TorrentFilter
{
    /// <summary>Parses a filter string, throwing <see cref="FilterParseException"/> on failure.</summary>
    public static IExpression Parse(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            throw new FilterParseException("Filter expression is empty.");

        var lexer = new TorrentFilterLexer(new AntlrInputStream(filter));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(ThrowingErrorListener.Instance);

        var parser = new TorrentFilterParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(ThrowingErrorListener.Instance);

        return new FilterExpressionBuilder().Visit(parser.filter());
    }

    /// <summary>Non-throwing variant: returns <c>false</c> and an error message on failure.</summary>
    public static bool TryParse(string filter, out IExpression? expression, out string? error)
    {
        try
        {
            expression = Parse(filter);
            error = null;
            return true;
        }
        catch (FilterParseException ex)
        {
            expression = null;
            error = ex.Message;
            return false;
        }
    }
}
