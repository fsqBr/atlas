using System.Text.Json;
using Atlas.Domain.Findings;
using Atlas.Scanner.Abstractions;

namespace Atlas.Application.Findings;

public sealed record SarifImport(
    string ScannerId,
    string ToolName,
    string ToolVersion,
    IReadOnlyList<RuleSpec> Rules,
    IReadOnlyList<FindingCandidate> Candidates);

/// <summary>
/// Reads a SARIF 2.1.0 log (ESLint, Semgrep, Trivy, CodeQL…) into Atlas finding candidates, so an
/// external tool's results live next to Atlas's own — reconciled, triaged, suppressed and scored
/// like any other finding. Each tool becomes its own scanner ("external.{tool}"), so a later import
/// from the same tool resolves what it no longer reports, and Atlas's scans never touch them.
/// </summary>
public static class SarifImporter
{
    public const int MaxResults = 20_000;

    private static readonly string[] SecurityTools = ["semgrep", "trivy", "codeql", "snyk", "bandit", "gitleaks", "checkov", "gosec", "brakeman", "zap"];

    public static SarifImport Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() == 0)
        {
            throw new ArgumentException("Not a SARIF log: no runs[].");
        }

        var run = runs[0];
        var driver = run.GetProperty("tool").GetProperty("driver");
        var toolName = driver.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "external";
        var toolVersion = driver.TryGetProperty("semanticVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!
            : driver.TryGetProperty("version", out var v2) && v2.ValueKind == JsonValueKind.String ? v2.GetString()! : "1.0.0";
        var slug = Slug(toolName);
        var scannerId = "external." + slug;
        var isSecurityTool = SecurityTools.Any(t => slug.Contains(t, StringComparison.Ordinal));

        // Rule metadata (optional in SARIF): titles and help text when the tool ships them.
        var metadata = new Dictionary<string, (string? Title, string? Description, string? Help)>(StringComparer.Ordinal);
        if (driver.TryGetProperty("rules", out var ruleList) && ruleList.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in ruleList.EnumerateArray())
            {
                if (rule.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.String)
                {
                    metadata[rid.GetString()!] = (
                        Text(rule, "shortDescription"),
                        Text(rule, "fullDescription") ?? Text(rule, "shortDescription"),
                        rule.TryGetProperty("helpUri", out var help) && help.ValueKind == JsonValueKind.String ? help.GetString() : null);
                }
            }
        }

        var candidates = new List<FindingCandidate>();
        var seenRules = new Dictionary<string, Severity>(StringComparer.Ordinal);
        if (run.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray().Take(MaxResults))
            {
                var sarifRuleId = result.TryGetProperty("ruleId", out var rr) && rr.ValueKind == JsonValueKind.String && rr.GetString()!.Length > 0
                    ? rr.GetString()!
                    : "result";
                var ruleId = scannerId + "." + Slug(sarifRuleId);
                var severity = MapSeverity(result);
                seenRules[ruleId] = seenRules.TryGetValue(ruleId, out var worst) && worst > severity ? worst : severity;

                string? file = null;
                int? line = null;
                if (result.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array && locations.GetArrayLength() > 0
                    && locations[0].TryGetProperty("physicalLocation", out var physical))
                {
                    if (physical.TryGetProperty("artifactLocation", out var artifact)
                        && artifact.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
                    {
                        file = uri.GetString()!.Replace('\\', '/').TrimStart('.', '/');
                    }

                    if (physical.TryGetProperty("region", out var region)
                        && region.TryGetProperty("startLine", out var start) && start.ValueKind == JsonValueKind.Number)
                    {
                        line = start.GetInt32();
                    }
                }

                var message = result.TryGetProperty("message", out var msg) && msg.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()!
                    : sarifRuleId;
                if (message.Length > 500)
                {
                    message = message[..500] + "…";
                }

                var meta = metadata.GetValueOrDefault(sarifRuleId);
                candidates.Add(new FindingCandidate(
                    ruleId, severity, ConfidenceLevel.Medium,
                    Title: meta.Title ?? sarifRuleId,
                    Message: $"{toolName}: {message}",
                    Evidence: new EvidenceCandidate(FilePath: file, LineStart: line, Symbol: file is null ? sarifRuleId : null),
                    Remediation: meta.Help,
                    Data: new Dictionary<string, string> { ["tool"] = toolName, ["sarifRuleId"] = sarifRuleId, ["message"] = message }));
            }
        }

        var specs = seenRules
            .Select(kv =>
            {
                var sarifRuleId = kv.Key[(scannerId.Length + 1)..];
                var meta = metadata.FirstOrDefault(m => Slug(m.Key) == sarifRuleId);
                return new RuleSpec(
                    kv.Key, "1.0.0",
                    isSecurityTool ? FindingCategory.Security : FindingCategory.Quality,
                    kv.Value,
                    meta.Value.Title ?? $"{toolName}: {meta.Key ?? sarifRuleId}",
                    meta.Value.Description ?? $"Imported from a {toolName} SARIF log.",
                    meta.Value.Help);
            })
            .ToList();

        return new SarifImport(scannerId, toolName, toolVersion, specs, candidates);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;

    private static Severity MapSeverity(JsonElement result)
    {
        // security-severity (GitHub convention) beats the coarse level when present.
        if (result.TryGetProperty("properties", out var props)
            && props.TryGetProperty("security-severity", out var sec)
            && (sec.ValueKind == JsonValueKind.Number && sec.TryGetDouble(out var score)
                || sec.ValueKind == JsonValueKind.String && double.TryParse(sec.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out score)))
        {
            return score switch { >= 9 => Severity.Critical, >= 7 => Severity.High, >= 4 => Severity.Medium, > 0 => Severity.Low, _ => Severity.Informational };
        }

        var level = result.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : "warning";
        return level switch
        {
            "error" => Severity.High,
            "warning" => Severity.Medium,
            "note" => Severity.Low,
            _ => Severity.Medium,
        };
    }

    private static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-");
        }

        return slug.Length == 0 ? "external" : slug.Length > 60 ? slug[..60] : slug;
    }
}
