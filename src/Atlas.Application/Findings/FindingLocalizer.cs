using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;

namespace Atlas.Application.Findings;

public sealed record LocalizedFindingText(string Title, string? Message, string? Remediation);

/// <summary>
/// Produces reader-language text for findings from rule localizations and the
/// finding's structured data. English is the canonical stored text; other
/// languages are rendered from templates, falling back to the localized rule
/// title and finally to the stored English — never to nothing.
/// </summary>
public static partial class FindingLocalizer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Placeholder();

    public static string Serialize(IReadOnlyDictionary<string, RuleLocalization>? localizations) =>
        localizations is null || localizations.Count == 0 ? "{}" : JsonSerializer.Serialize(localizations, Json);

    public static IReadOnlyDictionary<string, RuleLocalization> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new Dictionary<string, RuleLocalization>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, RuleLocalization>>(json, Json)
                   ?? new Dictionary<string, RuleLocalization>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, RuleLocalization>();
        }
    }

    public static bool IsEnglish(string? lang) =>
        string.IsNullOrWhiteSpace(lang) || lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the best localization for a language ("pt-BR" matches "pt-BR", then "pt").</summary>
    public static RuleLocalization? Localization(RuleDefinition? rule, string? lang)
    {
        if (rule is null || IsEnglish(lang))
        {
            return null;
        }

        var map = Deserialize(rule.LocalizationsJson);
        if (map.TryGetValue(lang!, out var exact))
        {
            return exact;
        }

        var prefix = lang!.Split('-')[0];
        return map.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Structured data of an occurrence (scanner-provided key/values), empty when absent or unreadable.</summary>
    public static IReadOnlyDictionary<string, string> Data(FindingOccurrence? occurrence)
    {
        if (occurrence is null || string.IsNullOrWhiteSpace(occurrence.DataJson))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(occurrence.DataJson, Json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string RuleDescription(RuleDefinition? rule, string? lang) =>
        Localization(rule, lang)?.Description ?? rule?.Description ?? string.Empty;

    public static string RuleTitle(RuleDefinition? rule, string ruleId, string? lang) =>
        Localization(rule, lang)?.Title ?? rule?.Title ?? ruleId;

    public static string? RuleRemediation(RuleDefinition? rule, string? lang) =>
        Localization(rule, lang)?.Remediation ?? rule?.Remediation;

    public static LocalizedFindingText Localize(
        Finding finding,
        FindingOccurrence? occurrence,
        RuleDefinition? rule,
        string? lang)
    {
        var localization = Localization(rule, lang);
        if (localization is null)
        {
            return new LocalizedFindingText(finding.Title, occurrence?.Message, occurrence?.Remediation ?? rule?.Remediation);
        }

        var values = BuildValues(occurrence);

        var title = localization.TitleTemplate is not null
            ? Fill(localization.TitleTemplate, values)
            : localization.Title;

        var message = localization.MessageTemplate is not null
            ? Fill(localization.MessageTemplate, values)
            : occurrence?.Message;

        return new LocalizedFindingText(title, message, localization.Remediation ?? occurrence?.Remediation ?? rule?.Remediation);
    }

    private static Dictionary<string, string> BuildValues(FindingOccurrence? occurrence)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (occurrence is null)
        {
            return values;
        }

        if (!string.IsNullOrWhiteSpace(occurrence.DataJson))
        {
            try
            {
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(occurrence.DataJson, Json) ?? [])
                {
                    values[kv.Key] = kv.Value;
                }
            }
            catch (JsonException)
            {
                // Data is optional; templates degrade to empty placeholders.
            }
        }

        var evidence = occurrence.Evidence;
        values["file"] = evidence.FilePath ?? string.Empty;
        values["fileName"] = evidence.FilePath is null ? string.Empty : Path.GetFileName(evidence.FilePath);
        values["line"] = evidence.LineStart?.ToString() ?? string.Empty;
        values["symbol"] = evidence.Symbol ?? string.Empty;
        return values;
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : string.Empty).Trim();
}
