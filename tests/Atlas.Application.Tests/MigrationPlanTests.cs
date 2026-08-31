using Atlas.Application.Ai;
using Atlas.Application.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Modernization;
using Atlas.Reporting;

namespace Atlas.Application.Tests;

/// <summary>Fact sheet for the AI migration plan, the Markdown subset renderer, and the report section that shows the draft.</summary>
public class MigrationPlanTests
{
    private const string Hostile = "<script>alert(1)</script>";

    private static ModernizationPlan SamplePlan()
    {
        var findings = new List<FindingFact>
        {
            new("dependency.migration-blocker.mb-001", Severity.High, FindingCategory.Modernization, "Web/Web.csproj", null),
            new("dependency.migration-blocker.mb-003", Severity.High, FindingCategory.Modernization, "Web/Web.csproj", null),
            new("security.sql.concatenation", Severity.Critical, FindingCategory.Security, "Data/Repo.cs", null),
            new("secrets.connection-string-password", Severity.High, FindingCategory.Secrets, "Web/web.config", null),
            new("dependency.package.vulnerable", Severity.High, FindingCategory.Dependencies, "Web/packages.config", null),
            new("quality.tests.none", Severity.Medium, FindingCategory.Quality, null, null),
        };
        var estate = new EstateFacts(120_000, 600, 300, 2000, 25, 6.5, 0.9, "SyntacticWithSymbols",
            [new ProjectSummary("Web", "v4.5.2", false, "WebForms"), new ProjectSummary("Data", "v4.5.2", false), new ProjectSummary("Core", "net8.0", true)]);
        var profile = ModernizationProfile.From(findings, estate);
        var assessment = ModernizationAnalyzer.Analyze(profile);
        var parameters = new CostParameters();
        var estimates = assessment.Strategies.Select(s => CostEngine.Estimate(profile, s.Strategy, parameters)).ToList();
        var roadmap = RoadmapBuilder.Build(profile, estimates.First(e => e.Strategy == assessment.Recommended));
        return new ModernizationPlan(profile, assessment, estimates, roadmap);
    }

    private static ExecutiveReport SampleReport() => new(
        Header: new ReportHeader("Acme", "Jane", $"Billing {Hostile}", "git", "https://example.test/acme/billing.git", "main", "abc123", "Completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        Scans: [],
        Inventory: [new ReportInventory("csharp", "SyntacticWithSymbols", 600, 120_000, 300, 2000, 25, 6.5, 0.9, 3, [])],
        Totals: new ReportTotals(6, 0, 0, Enum.GetValues<Severity>().ToDictionary(s => s, _ => 1), Enum.GetValues<FindingCategory>().ToDictionary(c => c, _ => 1)),
        RuleGroups: [new ReportRuleGroup("dependency.migration-blocker.mb-001", $"System.Web usage {Hostile}", FindingCategory.Modernization, Severity.High, 1, "Replace", ["Web/Web.csproj"])],
        Findings: [],
        Health: new ReportHealth(41, "High", "health.v1", "x", []),
        BusinessRules: [new ReportBusinessRule("Pricing.cs", "PricingService.Discount", 10, "Volume discount", "10% above 10 units", "Pricing", ["quantity > 10"], 0.9, "claude-sonnet-5")]);

    [Fact]
    public void Plan_facts_carry_profile_strategy_estimate_and_every_roadmap_phase()
    {
        var plan = SamplePlan();
        var facts = ReportNarrativeService.PlanFacts(SampleReport(), plan, ReportLocale.En);

        Assert.Contains("Health score: 41/100", facts); // the summary facts come first
        Assert.Contains("== Estate profile ==", facts);
        Assert.Contains("Lines of code: 120,000; projects: 3 (legacy framework 2, modern 1", facts);
        Assert.Contains("UI/hosting frameworks: WebForms ×1; projects with no upgrade path onto modern .NET: 1", facts);
        Assert.Contains($"Blocker dependency.migration-blocker.mb-001 (Prerequisite): System.Web usage {Hostile} — 1 occurrence(s).", facts);
        Assert.Contains("Security debt: critical 1, high 0", facts);
        Assert.Contains("Tests: none found", facts);
        Assert.Contains("RECOMMENDED: " + ModernizationTexts.Strategy(plan.Assessment.Recommended, "en"), facts);
        Assert.Contains("  Why: ", facts);
        Assert.Contains("== Estimate for the recommended strategy (cost.v1) ==", facts);
        Assert.Contains("Assumption: ", facts);
        Assert.Contains("== Roadmap (roadmap.v1", facts);
        foreach (var phase in plan.Roadmap.Phases)
        {
            Assert.Contains($"Phase {phase.Order + 1}: {ModernizationTexts.Text(phase.Key, "en")} — ", facts);
        }

        Assert.Contains("work items:", facts);
        Assert.Contains("Rule: Volume discount (Pricing) in PricingService.Discount.", facts);

        var pt = ReportNarrativeService.PlanFacts(SampleReport(), plan, ReportLocale.PtBr);
        Assert.Contains("RECOMMENDED: " + ModernizationTexts.Strategy(plan.Assessment.Recommended, "pt-BR"), pt); // labels stay English, strategy names follow the locale
        Assert.Contains(ModernizationTexts.Text(plan.Roadmap.Phases[0].Key, "pt-BR"), pt);
    }

    [Fact]
    public void Plan_instruction_demands_the_fixed_structure_in_both_languages()
    {
        var en = AiNarrativeService.PlanInstruction("en");
        var pt = AiNarrativeService.PlanInstruction("pt-BR");

        Assert.Contains("## Phases", en);
        Assert.Contains("## First 30 days", en);
        Assert.Contains("never invent numbers", en);
        Assert.Contains("## Fases", pt);
        Assert.Contains("## Primeiros 30 dias", pt);
        Assert.Contains("não invente números", pt);
    }

    [Fact]
    public void Mini_markdown_renders_the_subset_and_escapes_everything_else()
    {
        const string md = """
            # Title
            Intro with **bold** and `code` and <b>raw</b>.

            ## Phase 1
            - first item
            - second **item**
              continued here
            1. step one
            2) step two

            Closing paragraph
            spanning two lines.
            """;

        var html = MiniMarkdown.ToHtml(md, headingOffset: 1);

        Assert.Contains("<h2>Title</h2>", html);
        Assert.Contains("<h3>Phase 1</h3>", html);
        Assert.Contains("<p>Intro with <strong>bold</strong> and <code>code</code> and &lt;b&gt;raw&lt;/b&gt;.</p>", html);
        Assert.Contains("<ul><li>first item</li><li>second <strong>item</strong> continued here</li></ul>", html);
        Assert.Contains("<ol><li>step one</li><li>step two</li></ol>", html);
        Assert.Contains("<p>Closing paragraph spanning two lines.</p>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Equal(string.Empty, MiniMarkdown.ToHtml("   "));

        var plain = MiniMarkdown.ToPlainText(md);
        Assert.Contains("TITLE", plain);
        Assert.Contains("Intro with bold and code", plain);
        Assert.DoesNotContain("**", plain);
    }

    [Fact]
    public void Report_shows_the_plan_after_the_strategy_section_and_labels_it()
    {
        var plan = new ReportAiSummary($"## Objective\nMigrate {Hostile} safely.\n- keep shipping", "claude-sonnet-5", new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        var report = SampleReport() with { MigrationPlan = plan };

        var en = HtmlReportRenderer.Render(report, ReportLocale.En);
        Assert.Contains("Migration plan (AI draft)", en);
        Assert.Contains("<h3>Objective</h3>", en);
        Assert.Contains("<li>keep shipping</li>", en);
        Assert.Contains("Written by AI (claude-sonnet-5)", en);
        Assert.DoesNotContain("<script>", en);

        var pt = HtmlReportRenderer.Render(report, ReportLocale.PtBr);
        Assert.Contains("Plano de migração (rascunho por IA)", pt);

        Assert.DoesNotContain("Migration plan (AI draft)", HtmlReportRenderer.Render(SampleReport(), ReportLocale.En));
    }
}
