using Atlas.Application.Assessments;
using Atlas.Domain.Findings;
using Atlas.Reporting;

namespace Atlas.Application.Tests;

public class HtmlReportRendererTests
{
    private const string Hostile = "<script>alert('xss')</script>";

    private static ExecutiveReport Sample() => new(
        Header: new ReportHeader("Acme Consulting", "Jane <b>Doe</b>", $"Billing {Hostile}", "git",
            "https://example.test/acme/billing.git", "main", "abc123", "Completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        Scans:
        [
            new ReportScan("dependency.nuget", "0.1.0", "Succeeded", 17, 17, 0, 0, 0, null),
            new ReportScan("security.patterns", "0.1.0", "Failed", 0, 0, 0, 0, 0, $"boom {Hostile}"),
        ],
        Inventory:
        [
            new ReportInventory("csharp", "SyntacticWithSymbols", 422, 81620, 437, 2142, 31, 2.4, 0.97, 1,
                [new InventoryProjectEntry($"src/{Hostile}.csproj", Hostile, false, "v4.5", 25, 25, 1)]),
        ],
        Totals: new ReportTotals(3, 1, 0,
            Enum.GetValues<Severity>().ToDictionary(s => s, s => s == Severity.High ? 2 : s == Severity.Low ? 1 : 0),
            Enum.GetValues<FindingCategory>().ToDictionary(c => c, c => c == FindingCategory.Modernization ? 3 : 0)),
        RuleGroups:
        [
            new ReportRuleGroup("dependency.migration-blocker", $"Blocker {Hostile}", FindingCategory.Modernization,
                Severity.High, 2, $"Fix {Hostile}", [$"src/{Hostile}.csproj"]),
        ],
        Findings:
        [
            new ReportFinding("dependency.migration-blocker", $"Title {Hostile}", FindingCategory.Modernization,
                Severity.High, FindingStatus.Open, "High", $"src/{Hostile}.csproj", 12, "MB-003", $"Msg {Hostile}"),
        ],
        Health: new ReportHealth(47, "High", "health.v1", $"explanation {Hostile}",
        [
            new ReportHealthDimension("Security", 0.30, 12, 88, [$"Blocker {Hostile} ×2 (−16)"]),
            new ReportHealthDimension("Quality", 0.15, 100, 0, []),
        ]));

    [Fact]
    public void Encodes_every_hostile_string()
    {
        var html = HtmlReportRenderer.Render(Sample());

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>Doe</b>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Jane &lt;b&gt;Doe&lt;/b&gt;", html);
    }

    [Fact]
    public void Contains_all_sections_and_headline_numbers()
    {
        var html = HtmlReportRenderer.Render(Sample());

        foreach (var heading in new[] { "Executive summary", "Analysis coverage", "Key risks", "Security", "Modernization", "Dependencies", "Inventory", "Appendix A", "Appendix B" })
        {
            Assert.Contains($">{heading}", html);
        }

        Assert.Contains("81,620", html);
        Assert.Contains(">Health score<", html);
        Assert.Contains("<span class=\"score-v\">47</span>", html);
        Assert.Contains("risk-high", html);
        Assert.Contains("width:12%", html);
        Assert.Contains("Syntactic with symbols (no build)", html);
        Assert.Contains("97%", html);
        Assert.Contains("Acme Consulting", html);
        Assert.Contains("sev-high", html);
        Assert.Contains("st-failed", html);
    }

    [Fact]
    public void Renders_in_portuguese_with_localized_labels_and_numbers()
    {
        var html = HtmlReportRenderer.Render(Sample(), ReportLocale.PtBr);

        Assert.Contains("lang=\"pt-BR\"", html);
        Assert.Contains(">Índice de saúde<", html);
        Assert.Contains(">Sumário executivo<", html);
        Assert.Contains("81.620", html);
        Assert.Contains("Alto", html);
        Assert.Contains("Sintático com símbolos (sem build)", html);
        Assert.DoesNotContain(">Executive summary<", html);
    }

    [Fact]
    public void Locale_lookup_defaults_to_english()
    {
        Assert.Same(ReportLocale.En, ReportLocale.For(null));
        Assert.Same(ReportLocale.En, ReportLocale.For("de"));
        Assert.Same(ReportLocale.PtBr, ReportLocale.For("pt-BR"));
        Assert.Same(ReportLocale.PtBr, ReportLocale.For("pt"));
    }

    [Fact]
    public void Is_self_contained_without_scripts_or_external_resources()
    {
        var html = HtmlReportRenderer.Render(Sample());

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<!DOCTYPE html>", html);
    }

    [Fact]
    public void Renders_ai_summary_and_business_rules_labelled_as_ai_output_in_both_languages()
    {
        var report = Sample() with
        {
            AiSummary = new ReportAiSummary("Overall the estate is fragile.\n\nSecond paragraph " + Hostile, "claude-sonnet-5", new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero)),
            BusinessRules =
            [
                new ReportBusinessRule("src/Pricing.cs", "PricingService.Discount", 12, $"Volume discount {Hostile}", "Orders above 10 items get 5% off.", "Pricing", ["quantity > 10"], 0.9, "claude-sonnet-5"),
                new ReportBusinessRule("src/Loans.cs", "LoanApproval.Approve", 30, "Manager approval", "Loans above 50k need a manager.", "Eligibility", ["amount > 50000"], 0.8, "claude-sonnet-5"),
            ],
        };

        var en = HtmlReportRenderer.Render(report);
        Assert.Contains("Executive summary", en);
        Assert.Contains("Overall the estate is fragile.", en);
        Assert.Contains("Written by AI (claude-sonnet-5)", en);
        Assert.Contains("Business rules recovered from code", en);
        Assert.Contains("2 rule(s) recovered by AI", en);
        Assert.Contains("PricingService.Discount", en);
        Assert.Contains("quantity &gt; 10", en);
        Assert.Contains("90%", en);
        Assert.DoesNotContain("<script>", en);

        var pt = HtmlReportRenderer.Render(report, ReportLocale.PtBr);
        Assert.Contains("Resumo executivo", pt);
        Assert.Contains("Escrito por IA (claude-sonnet-5)", pt);
        Assert.Contains("Regras de negócio recuperadas do código", pt);

        var plain = HtmlReportRenderer.Render(Sample());
        Assert.DoesNotContain("Business rules recovered from code", plain);
        Assert.DoesNotContain("Written by AI", plain);
    }

    [Fact]
    public void Fact_sheet_for_the_summary_carries_the_headline_numbers_only()
    {
        var facts = ReportNarrativeService.Facts(Sample(), ReportLocale.En);

        Assert.Contains("Health score: 47/100; risk level: High.", facts);
        Assert.Contains("Open findings: 3; resolved: 1; suppressed: 0.", facts);
        Assert.Contains("Top rule: Blocker", facts);
        Assert.Contains("Projects: 1; source files: 422", facts);
        Assert.DoesNotContain("<script>", facts.Replace(Hostile, "")); // hostile strings pass through as data, never as markup
    }

    [Fact]
    public void Appendix_is_capped_and_the_pdf_footer_carries_brand_and_page_numbers()
    {
        var many = Enumerable.Range(0, HtmlReportRenderer.MaxAppendixRows + 40)
            .Select(i => new ReportFinding("rule.x", $"Finding {i}", FindingCategory.Quality, i % 7 == 0 ? Severity.Critical : Severity.Low, FindingStatus.Open, "High", $"src/F{i}.cs", i, null, "m"))
            .ToList();
        var report = Sample() with { Findings = many };

        var html = HtmlReportRenderer.Render(report, ReportLocale.PtBr);
        Assert.Contains($"Exibindo os {HtmlReportRenderer.MaxAppendixRows} findings mais severos de {many.Count}", html);
        Assert.Contains("Finding 0", html); // critical ones come first
        Assert.DoesNotContain("Finding 99<", html); // lowest-severity rows past the cap are left to the export

        var footer = HtmlReportRenderer.RenderPdfFooter(report, ReportLocale.PtBr);
        Assert.Contains("Acme Consulting", footer);
        Assert.Contains("Confidencial", footer);
        Assert.Contains("class=\"pageNumber\"", footer);
        Assert.Contains("class=\"totalPages\"", footer);
        Assert.DoesNotContain("<script>", footer);
    }
}
