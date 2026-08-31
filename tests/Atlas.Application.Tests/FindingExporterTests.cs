using System.Text.Json;
using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Domain.Tenants;

namespace Atlas.Application.Tests;

public class FindingExporterTests
{
    private static (IReadOnlyList<FindingWithLatestOccurrence> Items, IReadOnlyDictionary<string, RuleDefinition> Rules) Sample()
    {
        var assessmentId = Guid.NewGuid();
        var scanId = Guid.NewGuid();
        var finding = Finding.Create(Guid.NewGuid(), WellKnownTenants.DefaultId, assessmentId, "fp-1", "security.sql.concatenation", FindingCategory.Security,
            Severity.Critical, "SQL built by concatenation", FindingOrigin.Deterministic, scanId);
        var occurrence = new FindingOccurrence(Guid.NewGuid(), WellKnownTenants.DefaultId, finding.Id, scanId, Severity.Critical, ConfidenceLevel.High,
            "Query text is concatenated with \"user input\", risky", "Use parameters",
            new Evidence("security.patterns", "1.0", "src/Data/Repo.cs", 42, 42, "Repo.Load"), """{"method":"Load"}""");
        var suppressed = Finding.Create(Guid.NewGuid(), WellKnownTenants.DefaultId, assessmentId, "fp-2", "quality.file.large", FindingCategory.Quality,
            Severity.Low, "=HYPERLINK(\"x\") large file", FindingOrigin.Deterministic, scanId);
        suppressed.Suppress();

        var rule = new RuleDefinition("security.sql.concatenation", "security.patterns", "1.0", FindingCategory.Security, Severity.Critical,
            "SQL built by concatenation", "Concatenating input into SQL enables injection.", "Use parameterized queries.");

        return (
            [new FindingWithLatestOccurrence(finding, occurrence), new FindingWithLatestOccurrence(suppressed, null)],
            new Dictionary<string, RuleDefinition> { [rule.Id] = rule });
    }

    [Fact]
    public void Csv_quotes_and_neutralizes_formulas()
    {
        var (items, rules) = Sample();
        var csv = FindingExporter.ToCsv(items, rules, "en");
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("id,ruleId,category,severity,status", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"Query text is concatenated with \"\"user input\"\", risky\"", lines[1]);
        Assert.Contains("src/Data/Repo.cs,42,42,Repo.Load", lines[1]);
        Assert.Contains("'=HYPERLINK", lines[2]);
        Assert.Contains(",Suppressed,", lines[2]);
    }

    [Fact]
    public void Json_carries_structured_data()
    {
        var (items, rules) = Sample();
        using var doc = JsonDocument.Parse(FindingExporter.ToJson(items, rules, "en"));
        var first = doc.RootElement[0];
        Assert.Equal("security.sql.concatenation", first.GetProperty("ruleId").GetString());
        Assert.Equal("Load", first.GetProperty("data").GetProperty("method").GetString());
        Assert.Equal(42, first.GetProperty("lineStart").GetInt32());
    }

    [Fact]
    public void Sarif_is_2_1_0_with_rules_locations_and_suppressions()
    {
        var (items, rules) = Sample();
        using var doc = JsonDocument.Parse(FindingExporter.ToSarif(items, rules, "0.6.0", "en"));
        var root = doc.RootElement;

        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        var run = root.GetProperty("runs")[0];
        var driver = run.GetProperty("tool").GetProperty("driver");
        Assert.Equal("Atlas", driver.GetProperty("name").GetString());
        var ruleIds = driver.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Equal(["quality.file.large", "security.sql.concatenation"], ruleIds);

        var results = run.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        var sql = results.Single(r => r.GetProperty("ruleId").GetString() == "security.sql.concatenation");
        Assert.Equal("error", sql.GetProperty("level").GetString());
        Assert.Equal(1, sql.GetProperty("ruleIndex").GetInt32());
        Assert.Equal("src/Data/Repo.cs", sql.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal(42, sql.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region").GetProperty("startLine").GetInt32());
        Assert.Equal("fp-1", sql.GetProperty("partialFingerprints").GetProperty("atlas/v1").GetString());

        var large = results.Single(r => r.GetProperty("ruleId").GetString() == "quality.file.large");
        Assert.Equal("accepted", large.GetProperty("suppressions")[0].GetProperty("status").GetString());
        Assert.False(large.TryGetProperty("locations", out _));
    }
}
