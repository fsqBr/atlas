using System.Globalization;
using System.Text;
using System.Text.Json;
using Atlas.Application.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;

namespace Atlas.Application.Findings;

/// <summary>
/// Findings as files: CSV (spreadsheets), JSON (integrations) and SARIF 2.1.0
/// (GitHub code scanning, Azure DevOps, IDEs). Text is localized like the UI;
/// values originate in the analyzed repository and are treated as data.
/// </summary>
public static class FindingExporter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string ToCsv(IReadOnlyList<FindingWithLatestOccurrence> items, IReadOnlyDictionary<string, RuleDefinition> rules, string? lang)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,ruleId,category,severity,status,confidence,title,message,filePath,lineStart,lineEnd,symbol,remediation,createdAtUtc,updatedAtUtc");
        foreach (var item in items)
        {
            var f = item.Finding;
            var o = item.Latest;
            var text = FindingLocalizer.Localize(f, o, rules.GetValueOrDefault(f.RuleId), lang);
            sb.Append(Csv(f.Id.ToString())).Append(',')
              .Append(Csv(f.RuleId)).Append(',')
              .Append(Csv(f.Category.ToString())).Append(',')
              .Append(Csv(f.Severity.ToString())).Append(',')
              .Append(Csv(f.Status.ToString())).Append(',')
              .Append(Csv(o?.Confidence.ToString())).Append(',')
              .Append(Csv(text.Title)).Append(',')
              .Append(Csv(text.Message)).Append(',')
              .Append(Csv(o?.Evidence.FilePath)).Append(',')
              .Append(Csv(o?.Evidence.LineStart?.ToString(CultureInfo.InvariantCulture))).Append(',')
              .Append(Csv(o?.Evidence.LineEnd?.ToString(CultureInfo.InvariantCulture))).Append(',')
              .Append(Csv(o?.Evidence.Symbol)).Append(',')
              .Append(Csv(text.Remediation)).Append(',')
              .Append(Csv(f.CreatedAtUtc.ToString("O"))).Append(',')
              .Append(Csv(f.UpdatedAtUtc.ToString("O")))
              .AppendLine();
        }

        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<FindingWithLatestOccurrence> items, IReadOnlyDictionary<string, RuleDefinition> rules, string? lang)
    {
        var rows = items.Select(item =>
        {
            var f = item.Finding;
            var o = item.Latest;
            var text = FindingLocalizer.Localize(f, o, rules.GetValueOrDefault(f.RuleId), lang);
            return new
            {
                id = f.Id,
                ruleId = f.RuleId,
                category = f.Category.ToString(),
                severity = f.Severity.ToString(),
                status = f.Status.ToString(),
                confidence = o?.Confidence.ToString(),
                title = text.Title,
                message = text.Message,
                remediation = text.Remediation,
                filePath = o?.Evidence.FilePath,
                lineStart = o?.Evidence.LineStart,
                lineEnd = o?.Evidence.LineEnd,
                symbol = o?.Evidence.Symbol,
                data = FindingLocalizer.Data(o),
                createdAtUtc = f.CreatedAtUtc,
                updatedAtUtc = f.UpdatedAtUtc,
            };
        });
        return JsonSerializer.Serialize(rows, Json);
    }

    /// <summary>SARIF 2.1.0 with one run; suppressed/false-positive findings carry a suppression entry.</summary>
    public static string ToSarif(
        IReadOnlyList<FindingWithLatestOccurrence> items,
        IReadOnlyDictionary<string, RuleDefinition> rules,
        string toolVersion,
        string? lang)
    {
        var usedRuleIds = items.Select(i => i.Finding.RuleId).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();
        var ruleIndex = usedRuleIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);

        var sarifRules = usedRuleIds.Select(id =>
        {
            var rule = rules.GetValueOrDefault(id);
            var title = FindingLocalizer.RuleTitle(rule, id, lang);
            var remediation = FindingLocalizer.RuleRemediation(rule, lang);
            return new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = ToPascal(id),
                ["shortDescription"] = new { text = title },
                ["fullDescription"] = new { text = rule is null ? title : FindingLocalizer.RuleDescription(rule, lang) },
                ["help"] = remediation is null ? null : new { text = remediation },
                ["defaultConfiguration"] = new { level = Level(rule?.DefaultSeverity ?? Severity.Medium) },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["category"] = rule?.Category.ToString(),
                    ["tags"] = new[] { "atlas", rule?.Category.ToString().ToLowerInvariant() ?? "finding" },
                },
            };
        }).ToList();

        var results = items.Select(item =>
        {
            var f = item.Finding;
            var o = item.Latest;
            var text = FindingLocalizer.Localize(f, o, rules.GetValueOrDefault(f.RuleId), lang);
            var result = new Dictionary<string, object?>
            {
                ["ruleId"] = f.RuleId,
                ["ruleIndex"] = ruleIndex[f.RuleId],
                ["level"] = Level(f.Severity),
                ["message"] = new { text = string.IsNullOrWhiteSpace(text.Message) ? text.Title : text.Message },
                ["partialFingerprints"] = new Dictionary<string, string> { ["atlas/v1"] = f.Fingerprint },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["severity"] = f.Severity.ToString(),
                    ["status"] = f.Status.ToString(),
                    ["confidence"] = o?.Confidence.ToString(),
                    ["category"] = f.Category.ToString(),
                    ["symbol"] = o?.Evidence.Symbol,
                },
            };

            if (o?.Evidence.FilePath is { } path)
            {
                var region = o.Evidence.LineStart is { } line
                    ? new Dictionary<string, object?> { ["startLine"] = line, ["endLine"] = o.Evidence.LineEnd ?? line }
                    : null;
                result["locations"] = new object[]
                {
                    new
                    {
                        physicalLocation = new Dictionary<string, object?>
                        {
                            ["artifactLocation"] = new { uri = path.Replace('\\', '/').TrimStart('/'), uriBaseId = "%SRCROOT%" },
                            ["region"] = region,
                        },
                    },
                };
            }

            if (f.Status is FindingStatus.Suppressed or FindingStatus.FalsePositive)
            {
                result["suppressions"] = new object[]
                {
                    new { kind = "external", status = "accepted", justification = f.Status.ToString() },
                };
            }
            else if (f.Status == FindingStatus.Resolved)
            {
                result["baselineState"] = "absent";
            }

            return result;
        }).ToList();

        var log = new Dictionary<string, object?>
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tool"] = new
                    {
                        driver = new Dictionary<string, object?>
                        {
                            ["name"] = "Atlas",
                            ["version"] = toolVersion,
                            ["informationUri"] = "https://github.com/atlas-platform",
                            ["rules"] = sarifRules,
                        },
                    },
                    ["results"] = results,
                    ["columnKind"] = "utf16CodeUnits",
                },
            },
        };

        return JsonSerializer.Serialize(log, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    private static string Level(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => "error",
        Severity.Medium => "warning",
        _ => "note",
    };

    private static string ToPascal(string ruleId) =>
        string.Concat(ruleId.Split('.', '-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Neutralize spreadsheet formula injection from repository-controlled text.
        if (value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
