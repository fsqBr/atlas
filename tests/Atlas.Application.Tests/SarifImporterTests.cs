using Atlas.Application.Findings;
using Atlas.Domain.Findings;

namespace Atlas.Application.Tests;

public class SarifImporterTests
{
    private const string Log = """
        {
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Semgrep", "semanticVersion": "1.55.0", "rules": [
                { "id": "js.sqli", "shortDescription": { "text": "SQL injection" }, "helpUri": "https://example.com/sqli" }
              ] } },
              "results": [
                {
                  "ruleId": "js.sqli",
                  "level": "error",
                  "properties": { "security-severity": "9.8" },
                  "message": { "text": "Tainted data reaches the query." },
                  "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "src/db.js" }, "region": { "startLine": 42 } } } ]
                },
                {
                  "ruleId": "js.style",
                  "level": "note",
                  "message": { "text": "Prefer const." },
                  "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "src/app.js" }, "region": { "startLine": 7 } } } ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Parses_tool_rules_and_results_with_severity_mapping()
    {
        var import = SarifImporter.Parse(Log);

        Assert.Equal("external.semgrep", import.ScannerId);
        Assert.Equal("Semgrep", import.ToolName);
        Assert.Equal(2, import.Candidates.Count);
        Assert.Equal(2, import.Rules.Count);
        Assert.Equal(0, import.RunsIgnored);

        var sqli = import.Candidates.Single(c => c.RuleId == "external.semgrep.js-sqli");
        Assert.Equal(Severity.Critical, sqli.Severity);     // security-severity 9.8 beats level
        Assert.Equal("SQL injection", sqli.Title);
        Assert.Equal("src/db.js", sqli.Evidence.FilePath);
        Assert.Equal(42, sqli.Evidence.LineStart);
        Assert.Contains("Semgrep:", sqli.Message);

        var style = import.Candidates.Single(c => c.RuleId == "external.semgrep.js-style");
        Assert.Equal(Severity.Low, style.Severity);          // note → Low

        var rule = import.Rules.Single(r => r.Id == "external.semgrep.js-sqli");
        Assert.Equal(FindingCategory.Security, rule.Category); // semgrep is a security tool
        Assert.Equal("https://example.com/sqli", rule.Remediation);
    }

    [Fact]
    public void Garbage_is_rejected_loudly()
    {
        Assert.ThrowsAny<Exception>(() => SarifImporter.Parse("{}"));
        Assert.ThrowsAny<Exception>(() => SarifImporter.Parse("not json"));
    }

    [Fact]
    public void External_strings_are_clamped_to_the_catalog_column_sizes()
    {
        var longText = new string('x', 5000);
        var log = $$"""
            {
              "version": "2.1.0",
              "runs": [ {
                "tool": { "driver": { "name": "{{new string('t', 300)}}", "rules": [
                  { "id": "r1", "shortDescription": { "text": "{{longText}}" }, "fullDescription": { "text": "{{longText}}" } }
                ] } },
                "results": [ { "ruleId": "r1", "level": "error", "message": { "text": "{{longText}}" },
                  "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "{{new string('p', 2000)}}" } } } ] } ]
              } ]
            }
            """;

        var import = SarifImporter.Parse(log);
        var rule = Assert.Single(import.Rules);
        Assert.True(rule.Title.Length <= 250);
        Assert.True(rule.Description.Length <= 3000);
        var candidate = Assert.Single(import.Candidates);
        Assert.True(candidate.Title.Length <= 400);
        Assert.True(candidate.Message.Length <= 3000);
        Assert.True(candidate.Evidence.FilePath!.Length <= 900);
        Assert.True(import.ToolName.Length <= 100);
    }

    [Fact]
    public void Distinct_rule_ids_that_slug_identically_stay_distinct()
    {
        const string log = """
            {
              "version": "2.1.0",
              "runs": [ {
                "tool": { "driver": { "name": "eslint" } },
                "results": [
                  { "ruleId": "no.console", "level": "warning", "message": { "text": "a" },
                    "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "a.js" }, "region": { "startLine": 1 } } } ] },
                  { "ruleId": "no-console", "level": "warning", "message": { "text": "b" },
                    "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "a.js" }, "region": { "startLine": 2 } } } ] }
                ]
              } ]
            }
            """;

        var import = SarifImporter.Parse(log);
        Assert.Equal(2, import.Candidates.Select(c => c.RuleId).Distinct().Count());
        Assert.Equal(2, import.Rules.Count);
    }

    [Fact]
    public void Multi_run_logs_merge_same_tool_runs_and_count_foreign_ones()
    {
        const string log = """
            {
              "version": "2.1.0",
              "runs": [
                { "tool": { "driver": { "name": "eslint" } },
                  "results": [ { "ruleId": "a", "level": "note", "message": { "text": "a" } } ] },
                { "tool": { "driver": { "name": "eslint" } },
                  "results": [ { "ruleId": "b", "level": "note", "message": { "text": "b" } } ] },
                { "tool": { "driver": { "name": "trivy" } },
                  "results": [ { "ruleId": "c", "level": "error", "message": { "text": "c" } } ] }
              ]
            }
            """;

        var import = SarifImporter.Parse(log);
        Assert.Equal("external.eslint", import.ScannerId);
        Assert.Equal(2, import.Candidates.Count);   // both eslint runs
        Assert.Equal(1, import.RunsIgnored);        // the trivy run is reported, not silently dropped
    }
}
