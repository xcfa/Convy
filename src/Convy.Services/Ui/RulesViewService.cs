using System.Text.Json.Serialization;
using Convy.Services.Rules;

namespace Convy.Services.Ui;

/// <summary>The rules file and the rules in effect, for the web UI (read-only).</summary>
/// <param name="Path">Path of the rules file.</param>
/// <param name="Exists">Whether the file exists.</param>
/// <param name="Text">The file as it is now; <c>null</c> when missing or too large.</param>
/// <param name="Version">Version of the rules in effect; grows with every successful reload.</param>
/// <param name="Rules">The rules in effect, in order (the first match wins).</param>
/// <param name="Error">Why the file as it is now could not be loaded; the rules in effect are the previous ones.</param>
public sealed record RulesView(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("rules")] IReadOnlyList<RuleDto> Rules,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_at")] DateTimeOffset? ErrorAt);

/// <param name="Instruction">What an agent should do with a job's files first; such jobs are handed off, not placed.</param>
/// <param name="MatchesEverything">The rule has an empty condition (a catch-all).</param>
/// <param name="Unreachable">An earlier rule matches everything, so this one is never used.</param>
public sealed record RuleDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("condition")] string Condition,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("instruction")] string? Instruction,
    [property: JsonPropertyName("properties")] IReadOnlyList<string> Properties,
    [property: JsonPropertyName("matches_everything")] bool MatchesEverything,
    [property: JsonPropertyName("unreachable")] bool Unreachable);

/// <summary>Shows the routing rules in the web UI.</summary>
public sealed class RulesViewService
{
    /// <summary>Larger files are not sent to the browser.</summary>
    public const int MaxTextBytes = 1024 * 1024;

    private readonly IRulesProvider _rules;
    private readonly IRulesDiagnostics _diagnostics;

    public RulesViewService(IRulesProvider rules, IRulesDiagnostics diagnostics)
    {
        _rules = rules;
        _diagnostics = diagnostics;
    }

    public async Task<RulesView> GetAsync(CancellationToken cancellationToken)
    {
        // Reading the snapshot also picks up a file changed since the last sync.
        var snapshot = _rules.GetCurrent();
        var path = _diagnostics.Path;
        var file = new FileInfo(path);

        string? text = null;
        if (file.Exists && file.Length <= MaxTextBytes)
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        var catchAll = snapshot.Mappings.Rules.ToList().FindIndex(r => r.MatchesEverything);
        var rules = snapshot.Mappings.Rules
            .Select((r, i) => new RuleDto(
                r.Name,
                r.RawCondition,
                r.OutputPath,
                r.Instruction,
                r.ReferencedProperties.Order(StringComparer.Ordinal).ToList(),
                r.MatchesEverything,
                catchAll >= 0 && i > catchAll))
            .ToList();

        return new RulesView(path, file.Exists, text, snapshot.Version, rules, _diagnostics.LastError, _diagnostics.LastErrorAt);
    }
}
