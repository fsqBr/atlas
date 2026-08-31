using Atlas.Application.Assessments;
using Atlas.Domain.Findings;

namespace Atlas.Application.Tests;

public class PrCommentTests
{
    private static RunSummary Run(int number, int? score, string? sha = "abcdef1234567890") =>
        new(Guid.NewGuid(), number, sha, "Completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, score, 48, 3, 40, 5, 1, 9, 0);

    private static RuleDelta Rule(string id, string title, Severity sev, int count, params string[] where) =>
        new(id, title, FindingCategory.Security, sev, count, where);

    private static QualityGateResult Gate(bool passed, params string[] violations) =>
        new(passed, true, 56, new Dictionary<Severity, int> { [Severity.Critical] = 1, [Severity.High] = 14, [Severity.Medium] = 24, [Severity.Low] = 4, [Severity.Informational] = 5 }, violations, "High", 60);

    [Fact]
    public void Renders_verdict_health_delta_changes_and_links()
    {
        var comparison = new RunComparison(Run(8, 56), Run(7, 52), false, 4, [],
            Resolved: [Rule("quality.tests.none", "No tests", Severity.Medium, 1)],
            New: [Rule("sec.sql.string-concatenation", "SQL built | from strings", Severity.High, 2, "Shop.Web/Services/OrderService.cs:15", "Shop.Web/Repo.cs:9"), Rule("quality.complexity.method", "High complexity", Severity.Medium, 1, "Shop.Web/Checkout.cs:40")],
            Regressed: [], Inventory: null);
        var md = PrComment.Render(new PrCommentInput("Legacy Shop", Guid.Parse("11111111-1111-1111-1111-111111111111"), comparison,
            Gate(false, "15 open finding(s) at severity High or above: Critical 1, High 14.", "Health score 56 is below the minimum 60."),
            "https://atlas.example.com/", "v0.35", "en", "Two new SQL concatenations in OrderService; fix before merging.", "claude-sonnet-5"));

        Assert.StartsWith(PrComment.Marker, md);
        Assert.Contains("## ◈ Atlas · Legacy Shop — ❌ gate failed", md);
        Assert.Contains("**Health 56/100** (▲ +4 vs run #7) · risk **High** · run #8 `abcdef1234`", md);
        Assert.Contains("| **48** | 1 | 14 | 24 | 4 | 5 |", md);
        Assert.Contains("Since run #7: **3 new**, 1 resolved, 0 regressed.", md);
        Assert.Contains("### 🆕 New in this run (3)", md);
        Assert.Contains("| 🟠 High | SQL built \\| from strings ×2 <br><sub>`sec.sql.string-concatenation` · Security</sub> | `Shop.Web/Services/OrderService.cs:15` +1 |", md);
        Assert.Contains("- ✗ 15 open finding(s) at severity High or above", md);
        Assert.Contains("_fail on open findings ≥ High · minimum health 60_", md);
        Assert.Contains("> 🤖 Two new SQL concatenations", md);
        Assert.Contains("Written by AI (claude-sonnet-5)", md);
        Assert.Contains("[Open assessment](https://atlas.example.com/assessments/11111111-1111-1111-1111-111111111111)", md);
        Assert.Contains("report?lang=en", md);
        Assert.Contains("Atlas v0.35", md);
        Assert.DoesNotContain("### ↩️ Regressed", md);
    }

    [Fact]
    public void First_run_passing_gate_in_portuguese()
    {
        var comparison = new RunComparison(Run(1, 82, null), null, false, null, [], [], [], [], null);
        var gate = new QualityGateResult(true, true, 82, new Dictionary<Severity, int>(), [], "High", null);

        var md = PrComment.Render(new PrCommentInput("Loja", Guid.NewGuid(), comparison, gate, null, "v0.35", "pt-BR"));

        Assert.Contains("✅ gate aprovado", md);
        Assert.Contains("**Saúde 82/100** · risco **Baixo** · execução #1", md);
        Assert.Contains("_Primeira execução — sem baseline para comparar._", md);
        Assert.Contains("- ✓ Nenhuma violação.", md);
        Assert.DoesNotContain("[Abrir assessment]", md); // no public base URL configured
        Assert.DoesNotContain("🤖", md);
    }

    [Fact]
    public void No_completed_run_and_no_gate_rules_still_produce_a_comment()
    {
        var none = new QualityGateResult(false, false, null, new Dictionary<Severity, int>(), ["No completed run to evaluate."], null, null);
        var md = PrComment.Render(new PrCommentInput("X", Guid.NewGuid(), null, none, null, "v0.35", "en"));
        Assert.Contains("⏳ no completed run", md);
        Assert.Contains("no completed run for this assessment yet", md);

        var noRules = new QualityGateResult(true, true, 70, new Dictionary<Severity, int>(), [], null, null);
        var md2 = PrComment.Render(new PrCommentInput("X", Guid.NewGuid(), new RunComparison(Run(2, 70), Run(1, 70), true, 0, [], [], [], [], null), noRules, null, "v0.35", "en"));
        Assert.Contains("ℹ️ no gate configured", md2);
        Assert.Contains("(= vs run #1)", md2);
        Assert.Contains("Same commit as the previous run", md2);
        Assert.DoesNotContain("### Gate", md2);
    }

    [Fact]
    public void Caps_rows_and_escapes_markup()
    {
        var many = Enumerable.Range(0, 14).Select(n => Rule($"rule.{n}", $"<b>Title {n}</b>", Severity.Low, 1, $"F{n}.cs")).ToList();
        var md = PrComment.Render(new PrCommentInput("X", Guid.NewGuid(), new RunComparison(Run(2, 70), Run(1, 70), false, 0, [], [], many, [], null), Gate(true), null, "v0.35", "en"));
        Assert.Equal(PrComment.MaxRows, md.Split('\n').Count(l => l.StartsWith("| 🔵 Low |")));
        Assert.Contains("_… and 4 more rule(s)._", md);
        Assert.DoesNotContain("<b>", md);
        Assert.Contains("&lt;b&gt;Title 0&lt;/b&gt;", md);
    }
}
