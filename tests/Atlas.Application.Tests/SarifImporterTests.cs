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
}
