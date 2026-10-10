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

public sealed record RuleDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("condition")] string Condition,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("properties")] IReadOnlyList<string> Properties);

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

        var rules = snapshot.Mappings.Rules
            .Select(r => new RuleDto(r.Name, r.RawCondition, r.OutputPath, r.ReferencedProperties.Order(StringComparer.Ordinal).ToList()))
            .ToList();

        return new RulesView(path, file.Exists, text, snapshot.Version, rules, _diagnostics.LastError, _diagnostics.LastErrorAt);
    }
}
