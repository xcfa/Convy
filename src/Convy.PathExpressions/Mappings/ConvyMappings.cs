using Banned.Qbittorrent.Models.Torrent;
using Convy.PathExpressions.Parsing;

namespace Convy.PathExpressions.Mappings;

/// <summary>
/// An ordered set of <see cref="MappingRule"/>s loaded from ConvyMappings.ini.
///
/// File format — one rule per line:
/// <code>
///   # comment lines start with '#' or ';' and are ignored
///   Size > 100 && Tags.Contains(Test) && Category == Test => D:\Media\Test
///   State == downloading || Ratio >= 2.0                   => D:\Media\Done
/// </code>
/// The condition (left of <c>=&gt;</c>) is the filter DSL; the right side is the
/// destination path. Rules are evaluated top to bottom and the first match wins.
/// </summary>
public sealed class ConvyMappings
{
    private const string Separator = "=>";

    public IReadOnlyList<MappingRule> Rules { get; }

    private ConvyMappings(IReadOnlyList<MappingRule> rules) => Rules = rules;

    /// <summary>Returns the output path of the first rule that matches, or <c>null</c>.</summary>
    public string? Resolve(TorrentInfo info)
    {
        foreach (var rule in Rules)
        {
            if (rule.Matches(info))
                return rule.OutputPath;
        }

        return null;
    }

    public static ConvyMappings LoadFromFile(string path) =>
        Parse(File.ReadLines(path));

    public static ConvyMappings Parse(IEnumerable<string> lines)
    {
        var rules = new List<MappingRule>();
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();

            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            var sep = line.IndexOf(Separator, StringComparison.Ordinal);
            if (sep < 0)
                throw new FilterParseException(
                    $"Line {lineNumber}: missing '{Separator}' separator between condition and output path.");

            var condition = line[..sep].Trim();
            var output = line[(sep + Separator.Length)..].Trim();

            if (output.Length == 0)
                throw new FilterParseException($"Line {lineNumber}: output path is empty.");

            try
            {
                rules.Add(new MappingRule
                {
                    Condition = TorrentFilter.Parse(condition),
                    OutputPath = output,
                    RawCondition = condition,
                    LineNumber = lineNumber,
                });
            }
            catch (FilterParseException ex)
            {
                throw new FilterParseException($"Line {lineNumber}: {ex.Message}", ex);
            }
        }

        return new ConvyMappings(rules);
    }
}
