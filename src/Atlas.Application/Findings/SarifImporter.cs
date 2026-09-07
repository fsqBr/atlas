using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.Domain.Findings;
using Atlas.Scanner.Abstractions;

namespace Atlas.Application.Findings;

public sealed record SarifImport(
    string ScannerId,
    string ToolName,
    string ToolVersion,
    IReadOnlyList<RuleSpec> Rules,
    IReadOnlyList<FindingCandidate> Candidates,
    int RunsIgnored);

/// <summary>
/// Reads a SARIF 2.1.0 log (ESLint, Semgrep, Trivy, CodeQL…) into Atlas finding candidates, so an
/// external tool's results live next to Atlas's own — reconciled, triaged, suppressed and scored
/// like any other finding. Each tool becomes its own scanner ("external.{tool}"), so a later import
/// from the same tool resolves what it no longer reports, and Atlas's scans never touch them.
/// Every string is external input: lengths are clamped to the catalog/finding column sizes.
/// </summary>
public static class SarifImporter
{
    public const int MaxResults = 20_000;
    private const int MaxRuleMetadata = 50_000;

    private static readonly string[] SecurityTools = ["semgrep", "trivy", "codeql", "snyk", "bandit", "gitleaks", "checkov", "gosec", "brakeman", "zap"];

    public static SarifImport Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array || runs.GetArrayLength() == 0)
        {
            throw new ArgumentException("Not a SARIF log: no runs[].");
        }

        // Multi-run logs (merged exports, multi-target scans): every run of the FIRST run's tool is
        // imported; runs of other tools are counted and reported, never silently dropped.
        var first = runs[0];
        var driver = first.GetProperty("tool").GetProperty("driver");
        var rawToolName = driver.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "external";
        var toolName = Clamp(rawToolName, 100);
        var toolVersion = Clamp(driver.TryGetProperty("semanticVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!
            : driver.TryGetProperty("version", out var v2) && v2.ValueKind == JsonValueKind.String ? v2.GetString()! : "1.0.0", 50);
        var slug = Slug(toolName);
        var scannerId = "external." + slug;
        var isSecurityTool = SecurityTools.Any(t => slug.Contains(t, StringComparison.Ordinal));

        var sameToolRuns = new List<JsonElement>();
        var runsIgnored = 0;
        foreach (var run in runs.EnumerateArray())
        {
            var name = run.TryGetProperty("tool", out var tool) && tool.TryGetProperty("driver", out var d)
                && d.TryGetProperty("name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null;
            // Compare on the raw name: the display name is clamped to 100 chars, and a longer
            // driver name must still match its own runs.
            if (string.Equals(name ?? rawToolName, rawToolName, StringComparison.OrdinalIgnoreCase))
            {
                sameToolRuns.Add(run);
            }
            else
            {
                runsIgnored++;
            }
        }

        // Rule metadata (optional in SARIF), pre-indexed by slug — matching by recomputed slugs per
        // result was O(rules × results) and a CPU DoS on large logs.
        var metadataBySlug = new Dictionary<string, (string OriginalId, string? Title, string? Description, string? Help)>(StringComparer.Ordinal);
        foreach (var run in sameToolRuns)
        {
            if (!run.TryGetProperty("tool", out var tool) || !tool.TryGetProperty("driver", out var d)
                || !d.TryGetProperty("rules", out var ruleList) || ruleList.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var rule in ruleList.EnumerateArray().Take(MaxRuleMetadata))
            {
                if (rule.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.String)
                {
                    var originalId = rid.GetString()!;
                    metadataBySlug.TryAdd(RuleSlug(originalId, metadataBySlug), (
                        originalId,
                        Text(rule, "shortDescription"),
                        Text(rule, "fullDescription") ?? Text(rule, "shortDescription"),
                        rule.TryGetProperty("helpUri", out var help) && help.ValueKind == JsonValueKind.String ? Clamp(help.GetString()!, 1000) : null));
                }
            }
        }

        var candidates = new List<FindingCandidate>();
        var seenRules = new Dictionary<string, Severity>(StringComparer.Ordinal);
        var resultSlugs = new Dictionary<string, (string OriginalId, string? Title, string? Description, string? Help)>(metadataBySlug, StringComparer.Ordinal);
        foreach (var run in sameToolRuns)
        {
            if (candidates.Count >= MaxResults
                || !run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                if (candidates.Count >= MaxResults)
                {
                    break;
                }

                var sarifRuleId = result.TryGetProperty("ruleId", out var rr) && rr.ValueKind == JsonValueKind.String && rr.GetString()!.Length > 0
                    ? rr.GetString()!
                    : "result";
                // Distinct original ids must stay distinct rules: "no.console" vs "no-console", or two
                // long ids sharing a truncated prefix, get a deterministic hash suffix instead of merging.
                var ruleSlug = RuleSlug(sarifRuleId, resultSlugs);
                resultSlugs.TryAdd(ruleSlug, (sarifRuleId, null, null, null));
                var ruleId = scannerId + "." + ruleSlug;
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
                        file = Clamp(uri.GetString()!.Replace('\\', '/').TrimStart('.', '/'), 900);
                    }

                    if (physical.TryGetProperty("region", out var region)
                        && region.TryGetProperty("startLine", out var start) && start.ValueKind == JsonValueKind.Number)
                    {
                        line = start.GetInt32();
                    }
                }

                var message = result.TryGetProperty("message", out var msg) && msg.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? Clamp(text.GetString()!, 500)
                    : Clamp(sarifRuleId, 500);

                var meta = metadataBySlug.GetValueOrDefault(ruleSlug);
                candidates.Add(new FindingCandidate(
                    ruleId, severity, ConfidenceLevel.Medium,
                    Title: Clamp(meta.Title ?? sarifRuleId, 400),
                    Message: Clamp($"{toolName}: {message}", 3000),
                    Evidence: new EvidenceCandidate(FilePath: file, LineStart: line, Symbol: file is null ? Clamp(sarifRuleId, 900) : null),
                    Remediation: meta.Help,
                    Data: new Dictionary<string, string> { ["tool"] = toolName, ["sarifRuleId"] = Clamp(sarifRuleId, 400), ["message"] = message }));
            }
        }

        var specs = seenRules
            .Select(kv =>
            {
                var ruleSlug = kv.Key[(scannerId.Length + 1)..];
                var meta = metadataBySlug.GetValueOrDefault(ruleSlug);
                var original = meta.OriginalId ?? resultSlugs.GetValueOrDefault(ruleSlug).OriginalId ?? ruleSlug;
                return new RuleSpec(
                    kv.Key, "1.0.0",
                    isSecurityTool ? FindingCategory.Security : FindingCategory.Quality,
                    kv.Value,
                    Clamp(meta.Title ?? $"{toolName}: {original}", 250),
                    Clamp(meta.Description ?? $"Imported from a {toolName} SARIF log.", 3000),
                    meta.Help);
            })
            .ToList();

        return new SarifImport(scannerId, toolName, toolVersion, specs, candidates, runsIgnored);
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

    /// <summary>The slug for one original rule id, made collision-proof against ids already slugged
    /// differently: a deterministic hash suffix keeps distinct rules distinct across imports.</summary>
    private static string RuleSlug(string originalId, Dictionary<string, (string OriginalId, string? Title, string? Description, string? Help)> taken)
    {
        var slug = Slug(originalId);
        if (taken.TryGetValue(slug, out var existing) && !string.Equals(existing.OriginalId, originalId, StringComparison.Ordinal))
        {
            slug = $"{(slug.Length > 53 ? slug[..53] : slug)}-{ShortHash(originalId)}";
        }

        return slug;
    }

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..6];

    private static string Clamp(string value, int max) => value.Length <= max ? value : value[..max];

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
