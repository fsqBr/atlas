using System.Globalization;
using System.Text;
using Atlas.Domain.Findings;

namespace Atlas.Reporting;

/// <summary>
/// Self-contained, print-friendly HTML. No scripts, no external
/// resources. Every dynamic string is HTML-encoded: file names, titles and
/// messages originate in the analyzed repository and are hostile input
/// (threat model 5.6 — stored XSS via evidence). Labels come from ReportLocale.
/// </summary>
public static class HtmlReportRenderer
{
    /// <summary>Appendix A is a reading aid, not the export: everything beyond this goes to CSV/JSON/SARIF.</summary>
    public const int MaxAppendixRows = 250;

    public static string Render(ExecutiveReport report, ReportLocale? locale = null, ReportOptions? options = null)
    {
        var l = locale ?? ReportLocale.En;
        var c = l.Culture;
        var sb = new StringBuilder(64 * 1024);
        var h = report.Header;

        sb.Append("<!DOCTYPE html><html lang=\"").Append(E(l.Code)).Append("\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(E(h.AssessmentName)).Append(" — ").Append(E(l.DocumentSuffix)).Append("</title>")
          .Append("<style>").Append(Css);
        if (options?.AccentColor is { } accent && System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9A-Fa-f]{6}$"))
        {
            sb.Append(":root{--accent:").Append(accent).Append('}');
        }

        sb.Append("</style></head><body><main class=\"page\">");

        RenderHeader(sb, h, l, c, options?.LogoDataUri);
        RenderExecutive(sb, report, l, c);
        RenderAiSummary(sb, report.AiSummary, l, c);
        RenderHealth(sb, report.Health, l, c);
        RenderSummary(sb, report, l, c);
        RenderCoverage(sb, report, l, c);
        RenderKeyRisks(sb, report, l, c);
        RenderModernization(sb, report.Modernization, l, c);
        RenderMigrationPlan(sb, report.MigrationPlan, l, c);
        RenderBusinessRules(sb, report.BusinessRules, l, c);
        RenderCategory(sb, report, l, c, l.Security, FindingCategory.Security, FindingCategory.Secrets);
        RenderCategory(sb, report, l, c, l.Modernization, FindingCategory.Modernization);
        RenderCategory(sb, report, l, c, l.Dependencies, FindingCategory.Dependencies);
        RenderInventory(sb, report, l, c);
        RenderProjects(sb, report, l, c);
        RenderAppendix(sb, report, l);

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void RenderHeader(StringBuilder sb, ReportHeader h, ReportLocale l, CultureInfo c, string? logoDataUri = null)
    {
        sb.Append("<header class=\"head\">");
        if (logoDataUri is not null && logoDataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !logoDataUri.Contains('<'))
        {
            sb.Append("<img class=\"logo\" alt=\"\" src=\"").Append(E(logoDataUri)).Append("\">");
        }

        sb.Append("<p class=\"eyebrow\">").Append(E(h.BrandName)).Append(" · ").Append(E(l.Eyebrow)).Append("</p>")
          .Append("<h1>").Append(E(h.AssessmentName)).Append("</h1>")
          .Append("<dl class=\"meta\">")
          .Append("<dt>").Append(E(l.Source)).Append("</dt><dd>").Append(E(h.SourceKind)).Append(" · ").Append(E(h.SourceLocator));
        if (h.Branch is not null)
        {
            sb.Append(" @ ").Append(E(h.Branch));
        }

        sb.Append("</dd>");
        if (h.CommitSha is not null)
        {
            sb.Append("<dt>").Append(E(l.Commit)).Append("</dt><dd class=\"mono\">").Append(E(h.CommitSha)).Append("</dd>");
        }

        sb.Append("<dt>").Append(E(l.Status)).Append("</dt><dd>").Append(E(l.Term(h.Status))).Append("</dd>")
          .Append("<dt>").Append(E(l.Generated)).Append("</dt><dd>").Append(h.GeneratedAtUtc.ToString("g", c)).Append(" UTC</dd>");
        if (h.PreparedBy is not null)
        {
            sb.Append("<dt>").Append(E(l.PreparedBy)).Append("</dt><dd>").Append(E(h.PreparedBy)).Append("</dd>");
        }

        sb.Append("</dl></header>");
    }

    private static void RenderHealth(StringBuilder sb, ReportHealth? health, ReportLocale l, CultureInfo c)
    {
        sb.Append("<section class=\"health\"><h2>").Append(E(l.HealthScore)).Append("</h2>");
        if (health is null)
        {
            sb.Append("<p class=\"muted\">").Append(E(l.NoHealth)).Append("</p></section>");
            return;
        }

        var level = health.RiskLevel.ToLowerInvariant();
        sb.Append("<div class=\"health-hero\"><div class=\"score risk-").Append(E(level)).Append("\">")
          .Append("<span class=\"score-v\">").Append(health.Score.ToString(c)).Append("</span><span class=\"score-d\">/100</span></div>")
          .Append("<div><p class=\"risk-label\">").Append(E(l.OverallRisk)).Append(": <strong class=\"risk-").Append(E(level)).Append("\">").Append(E(l.Term(health.RiskLevel))).Append("</strong></p>")
          .Append("<p class=\"muted small\">").Append(E(health.Explanation)).Append("</p></div></div>");

        sb.Append("<table><thead><tr><th>").Append(E(l.Dimension)).Append("</th><th class=\"num\">").Append(E(l.Weight)).Append("</th><th class=\"num\">").Append(E(l.Score))
          .Append("</th><th>").Append(E(l.Bar)).Append("</th><th class=\"num\">").Append(E(l.Penalty)).Append("</th><th>").Append(E(l.MainContributors)).Append("</th></tr></thead><tbody>");
        foreach (var d in health.Dimensions)
        {
            sb.Append("<tr><td><strong>").Append(E(l.Term(d.Name))).Append("</strong></td>")
              .Append("<td class=\"num\">").Append((d.Weight * 100).ToString("0", c)).Append("%</td>")
              .Append("<td class=\"num\">").Append(d.Score.ToString(c)).Append("</td>")
              .Append("<td><div class=\"bar\"><div class=\"bar-fill bar-").Append(d.Score < 40 ? "critical" : d.Score < 60 ? "high" : d.Score < 80 ? "medium" : "low")
              .Append("\" style=\"width:").Append(d.Score.ToString(CultureInfo.InvariantCulture)).Append("%\"></div></div></td>")
              .Append("<td class=\"num\">−").Append(d.Penalty.ToString("0.#", c)).Append("</td>")
              .Append("<td class=\"small\">").Append(d.Contributors.Count == 0 ? "<span class=\"muted\">" + E(l.None) + "</span>" : string.Join("<br>", d.Contributors.Select(E))).Append("</td></tr>");
        }

        sb.Append("</tbody></table><p class=\"muted small\">").Append(E(l.HealthFormula)).Append("</p></section>");
    }

    private static void RenderSummary(StringBuilder sb, ExecutiveReport r, ReportLocale l, CultureInfo c)
    {
        var t = r.Totals;
        var files = r.Inventory.Sum(i => i.Files);
        var lines = r.Inventory.Sum(i => i.Lines);
        var projects = r.Inventory.Sum(i => i.Projects.Count);

        sb.Append("<section><h2>").Append(E(l.ExecutiveSummary)).Append("</h2><div class=\"tiles\">");
        Tile(sb, l.OpenFindings, t.Open.ToString("N0", c), null);
        Tile(sb, l.CriticalHigh, (t.OpenBySeverity[Severity.Critical] + t.OpenBySeverity[Severity.High]).ToString("N0", c), "critical");
        Tile(sb, l.Medium, t.OpenBySeverity[Severity.Medium].ToString("N0", c), "medium");
        Tile(sb, l.LowInfo, (t.OpenBySeverity[Severity.Low] + t.OpenBySeverity[Severity.Informational]).ToString("N0", c), "low");
        Tile(sb, l.Projects, projects.ToString("N0", c), null);
        Tile(sb, l.SourceFiles, files.ToString("N0", c), null);
        Tile(sb, l.LinesOfCode, lines.ToString("N0", c), null);
        Tile(sb, l.ResolvedSinceFirst, t.Resolved.ToString("N0", c), null);
        sb.Append("</div>");

        sb.Append("<table class=\"kv\"><tr><th>").Append(E(l.ByCategory)).Append("</th><td>");
        sb.Append(string.Join(" · ", t.OpenByCategory.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{E(l.Term(kv.Key.ToString()))} <b>{kv.Value.ToString("N0", c)}</b>")));
        sb.Append("</td></tr></table>");

        sb.Append("<aside class=\"note\">").Append(l.WhatThisReportIs).Append(' ').Append(l.NotIncluded).Append("</aside></section>");
    }

    private static void RenderCoverage(StringBuilder sb, ExecutiveReport r, ReportLocale l, CultureInfo c)
    {
        sb.Append("<section><h2>").Append(E(l.AnalysisCoverage)).Append("</h2>");

        sb.Append("<table><thead><tr><th>").Append(E(l.Language)).Append("</th><th>").Append(E(l.DepthAchieved)).Append("</th><th class=\"num\">").Append(E(l.Files))
          .Append("</th><th class=\"num\">").Append(E(l.Lines)).Append("</th><th class=\"num\">").Append(E(l.Types)).Append("</th><th class=\"num\">").Append(E(l.Methods))
          .Append("</th><th class=\"num\">").Append(E(l.SymbolsResolved)).Append("</th></tr></thead><tbody>");
        foreach (var i in r.Inventory)
        {
            sb.Append("<tr><td>").Append(E(i.LanguageId)).Append("</td><td>").Append(E(l.Term(i.Tier))).Append("</td>")
              .Append("<td class=\"num\">").Append(i.Files.ToString("N0", c)).Append("</td>")
              .Append("<td class=\"num\">").Append(i.Lines.ToString("N0", c)).Append("</td>")
              .Append("<td class=\"num\">").Append(i.Types.ToString("N0", c)).Append("</td>")
              .Append("<td class=\"num\">").Append(i.Methods.ToString("N0", c)).Append("</td>")
              .Append("<td class=\"num\">").Append(i.SymbolResolutionRate is { } rate ? (rate * 100).ToString("F0", c) + "%" : "—").Append("</td></tr>");
        }

        if (r.Inventory.Count == 0)
        {
            sb.Append("<tr><td colspan=\"7\" class=\"muted\">").Append(E(l.NoLanguage)).Append("</td></tr>");
        }

        sb.Append("</tbody></table>");

        sb.Append("<table><thead><tr><th>").Append(E(l.Scanner)).Append("</th><th>").Append(E(l.Version)).Append("</th><th>").Append(E(l.Status))
          .Append("</th><th class=\"num\">").Append(E(l.Emitted)).Append("</th><th class=\"num\">").Append(E(l.New)).Append("</th><th class=\"num\">").Append(E(l.Recurring))
          .Append("</th><th class=\"num\">").Append(E(l.Resolved)).Append("</th><th class=\"num\">").Append(E(l.Regressed)).Append("</th></tr></thead><tbody>");
        foreach (var s in r.Scans)
        {
            sb.Append("<tr><td class=\"mono\">").Append(E(s.ScannerId)).Append("</td><td class=\"mono\">").Append(E(s.Version)).Append("</td>")
              .Append("<td>").Append(StatusChip(s.Status, l)).Append(s.Error is null ? string.Empty : " <span class=\"muted\">" + E(Truncate(s.Error, 120)) + "</span>").Append("</td>")
              .Append("<td class=\"num\">").Append(s.Emitted).Append("</td><td class=\"num\">").Append(s.New).Append("</td>")
              .Append("<td class=\"num\">").Append(s.Recurring).Append("</td><td class=\"num\">").Append(s.Resolved).Append("</td>")
              .Append("<td class=\"num\">").Append(s.Regressed).Append("</td></tr>");
        }

        sb.Append("</tbody></table></section>");
    }

    private static void RenderKeyRisks(StringBuilder sb, ExecutiveReport r, ReportLocale l, CultureInfo c)
    {
        sb.Append("<section><h2>").Append(E(l.KeyRisks)).Append("</h2>");
        if (r.RuleGroups.Count == 0)
        {
            sb.Append("<p class=\"muted\">").Append(E(l.NoOpenFindings)).Append("</p></section>");
            return;
        }

        sb.Append("<ol class=\"risks\">");
        foreach (var g in r.RuleGroups.Take(10))
        {
            sb.Append("<li>").Append(SeverityChip(g.MaxSeverity, l)).Append(" <strong>").Append(E(g.Title)).Append("</strong>")
              .Append(" <span class=\"muted\">").Append(g.OpenCount.ToString("N0", c)).Append(' ').Append(E(g.OpenCount == 1 ? l.Finding : l.Findings))
              .Append(" · ").Append(E(l.Term(g.Category.ToString()))).Append("</span>");
            if (g.Remediation is not null)
            {
                sb.Append("<div class=\"rem\">").Append(E(g.Remediation)).Append("</div>");
            }

            sb.Append("</li>");
        }

        sb.Append("</ol></section>");
    }

    private static void RenderCategory(StringBuilder sb, ExecutiveReport r, ReportLocale l, CultureInfo c, string title, params FindingCategory[] categories)
    {
        var groups = r.RuleGroups.Where(g => categories.Contains(g.Category)).ToList();
        sb.Append("<section><h2>").Append(E(title)).Append("</h2>");
        if (groups.Count == 0)
        {
            sb.Append("<p class=\"muted\">").Append(E(l.NoFindingsInArea)).Append("</p></section>");
            return;
        }

        sb.Append("<table><thead><tr><th>").Append(E(l.Severity)).Append("</th><th>").Append(E(l.Rule)).Append("</th><th class=\"num\">").Append(E(l.Open))
          .Append("</th><th>").Append(E(l.WhereSample)).Append("</th></tr></thead><tbody>");
        foreach (var g in groups)
        {
            sb.Append("<tr><td>").Append(SeverityChip(g.MaxSeverity, l)).Append("</td>")
              .Append("<td><strong>").Append(E(g.Title)).Append("</strong><br><span class=\"mono muted\">").Append(E(g.RuleId)).Append("</span></td>")
              .Append("<td class=\"num\">").Append(g.OpenCount.ToString("N0", c)).Append("</td>")
              .Append("<td class=\"mono small\">").Append(string.Join("<br>", g.SampleLocations.Select(E))).Append("</td></tr>");
        }

        sb.Append("</tbody></table></section>");
    }

    /// <summary>Page one: the verdict, five numbers, what changed since the previous run and the top risks.</summary>
    private static void RenderExecutive(StringBuilder sb, ExecutiveReport r, ReportLocale l, CultureInfo c)
    {
        sb.Append("<section class=\"exec\"><h2>").Append(E(l.ExecutivePage)).Append("</h2>");
        if (r.Verdict is not null)
        {
            sb.Append("<p class=\"verdict\">").Append(E(r.Verdict)).Append("</p>");
        }

        var t = r.Totals;
        sb.Append("<div class=\"tiles\">");
        var scoreValue = r.Health is null ? "—" : r.Health.Score.ToString(c) + (r.Comparison?.HealthDelta is { } d && d != 0 ? (d > 0 ? " ▲" : " ▼") + Math.Abs(d).ToString(c) : string.Empty);
        Tile(sb, l.HealthScore, scoreValue, r.Health is null ? null : r.Health.Score < 40 ? "critical" : r.Health.Score < 60 ? "medium" : r.Health.Score < 80 ? "low" : null);
        Tile(sb, l.OpenFindings, t.Open.ToString("N0", c), null);
        Tile(sb, l.CriticalHigh, (t.OpenBySeverity[Severity.Critical] + t.OpenBySeverity[Severity.High]).ToString("N0", c), "critical");
        if (r.Modernization is not null)
        {
            Tile(sb, l.RecommendedStrategy, r.Modernization.RecommendedName, null);
            var e = r.Modernization.RecommendedEstimate;
            Tile(sb, l.EffortRange, $"{e.OptimisticHours.ToString("N0", c)} – {e.ConservativeHours.ToString("N0", c)} {l.Hours}", null);
        }

        sb.Append("</div>");

        if (r.Comparison is { } cmp)
        {
            sb.Append("<p class=\"since\"><strong>").Append(E(string.Format(c, l.SincePrevious, cmp.PreviousRun))).Append(":</strong> ")
              .Append(cmp.Resolved.ToString("N0", c)).Append(' ').Append(E(l.ResolvedLabel)).Append(" · ")
              .Append(cmp.New.ToString("N0", c)).Append(' ').Append(E(l.NewLabel)).Append(" · ")
              .Append(cmp.Regressed.ToString("N0", c)).Append(' ').Append(E(l.RegressedLabel)).Append("</p>");
            if (cmp.TopResolved.Count + cmp.TopNew.Count > 0)
            {
                sb.Append("<div class=\"compare\">");
                if (cmp.TopResolved.Count > 0)
                {
                    sb.Append("<div><h3 class=\"up\">").Append(E(l.ResolvedLabel)).Append("</h3><ul class=\"small\">").Append(string.Join(string.Empty, cmp.TopResolved.Select(x => "<li>" + E(x) + "</li>"))).Append("</ul></div>");
                }

                if (cmp.TopNew.Count > 0)
                {
                    sb.Append("<div><h3 class=\"down\">").Append(E(l.NewLabel)).Append("</h3><ul class=\"small\">").Append(string.Join(string.Empty, cmp.TopNew.Select(x => "<li>" + E(x) + "</li>"))).Append("</ul></div>");
                }

                sb.Append("</div>");
            }
        }
        else
        {
            sb.Append("<p class=\"muted small\">").Append(E(l.FirstRun)).Append("</p>");
        }

        if (r.RuleGroups.Count > 0)
        {
            sb.Append("<h3>").Append(E(l.TopRisksShort)).Append("</h3><ol class=\"risks compact\">");
            foreach (var g in r.RuleGroups.Take(5))
            {
                sb.Append("<li>").Append(SeverityChip(g.MaxSeverity, l)).Append(" <strong>").Append(E(g.Title)).Append("</strong> <span class=\"muted\">×").Append(g.OpenCount.ToString("N0", c)).Append("</span></li>");
            }

            sb.Append("</ol>");
        }

        sb.Append("</section><div class=\"pagebreak\"></div>");
    }

    private static void RenderProjects(StringBuilder sb, ExecutiveReport r, ReportLocale l, CultureInfo c)
    {
        sb.Append("<section><h2>").Append(E(l.ByProject)).Append("</h2>");
        if (r.Projects is null || r.Projects.Count == 0)
        {
            sb.Append("<p class=\"muted\">").Append(E(l.NoProjects)).Append("</p></section>");
            return;
        }

        sb.Append("<table class=\"dense\"><thead><tr><th>").Append(E(l.Project)).Append("</th><th>").Append(E(l.TargetFramework)).Append("</th><th>").Append(E(l.UiFramework)).Append("</th><th class=\"num\">").Append(E(l.Open))
          .Append("</th><th class=\"num\">").Append(E(l.Term("Critical"))).Append("</th><th class=\"num\">").Append(E(l.Term("High"))).Append("</th><th class=\"num\">").Append(E(l.Term("Medium"))).Append("</th><th class=\"num\">").Append(E(l.Term("Low"))).Append("</th></tr></thead><tbody>");
        foreach (var p in r.Projects)
        {
            sb.Append("<tr><td><strong>").Append(E(p.Project)).Append("</strong></td><td class=\"mono small\">").Append(E(p.TargetFramework ?? "—")).Append("</td><td class=\"small\">").Append(E(p.UiFramework ?? "—")).Append("</td>")
              .Append("<td class=\"num\">").Append(p.Open.ToString("N0", c)).Append("</td><td class=\"num\">").Append(p.Critical.ToString(c)).Append("</td><td class=\"num\">").Append(p.High.ToString(c))
              .Append("</td><td class=\"num\">").Append(p.Medium.ToString(c)).Append("</td><td class=\"num\">").Append(p.Low.ToString(c)).Append("</td></tr>");
        }

        sb.Append("</tbody></table></section>");
    }

    private static void RenderAiSummary(StringBuilder sb, ReportAiSummary? summary, ReportLocale l, CultureInfo c)
    {
        if (summary is null)
        {
            return;
        }

        sb.Append("<section class=\"ai-summary\"><h2>").Append(E(l.AiSummaryTitle)).Append("</h2>");
        foreach (var paragraph in summary.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            sb.Append("<p>").Append(E(paragraph)).Append("</p>");
        }

        sb.Append("<p class=\"muted small ai-label\">").Append(E(string.Format(c, l.AiGenerated, summary.Model, summary.CreatedAtUtc.ToString("d", c)))).Append("</p></section>");
    }

    /// <summary>The model's plan draft, right after the deterministic strategy/roadmap section it was written from. Markdown subset → escaped HTML.</summary>
    private static void RenderMigrationPlan(StringBuilder sb, ReportAiSummary? plan, ReportLocale l, CultureInfo c)
    {
        if (plan is null)
        {
            return;
        }

        sb.Append("<section class=\"ai-plan\"><h2>").Append(E(l.MigrationPlanTitle)).Append("</h2>")
          .Append(MiniMarkdown.ToHtml(plan.Text, headingOffset: 1))
          .Append("<p class=\"muted small ai-label\">").Append(E(string.Format(c, l.AiGenerated, plan.Model, plan.CreatedAtUtc.ToString("d", c)))).Append("</p></section>");
    }

    private static void RenderBusinessRules(StringBuilder sb, IReadOnlyList<ReportBusinessRule>? rules, ReportLocale l, CultureInfo c)
    {
        if (rules is null || rules.Count == 0)
        {
            return;
        }

        const int max = 60;
        var models = string.Join(", ", rules.Select(r => r.Model).Distinct());
        sb.Append("<section class=\"business-rules\"><h2>").Append(E(l.BusinessRulesTitle)).Append("</h2>")
          .Append("<p class=\"muted\">").Append(E(string.Format(c, l.BusinessRulesIntro, rules.Count, models))).Append("</p>");

        foreach (var group in rules.GroupBy(r => r.Category).OrderByDescending(g => g.Count()))
        {
            sb.Append("<h3>").Append(E(l.Term(group.Key))).Append(" <span class=\"muted\">(").Append(group.Count().ToString(c)).Append(")</span></h3>");
            sb.Append("<table><thead><tr><th>").Append(E(l.RuleColumn)).Append("</th><th>").Append(E(l.WhereColumn)).Append("</th><th>").Append(E(l.ConditionsColumn)).Append("</th><th class=\"num\">").Append(E(l.ConfidenceColumn)).Append("</th></tr></thead><tbody>");
            foreach (var r in group.Take(max))
            {
                sb.Append("<tr><td><strong>").Append(E(r.Name)).Append("</strong><br><span class=\"small\">").Append(E(r.Description)).Append("</span></td>")
                  .Append("<td class=\"mono small\">").Append(E(r.FilePath)).Append("<br>").Append(E(r.Symbol)).Append(':').Append(r.StartLine.ToString(c)).Append("</td>")
                  .Append("<td class=\"small\">").Append(E(string.Join("; ", r.Conditions))).Append("</td>")
                  .Append("<td class=\"num\">").Append(Math.Round(r.Confidence * 100).ToString(c)).Append("%</td></tr>");
            }

            sb.Append("</tbody></table>");
            if (group.Count() > max)
            {
                sb.Append("<p class=\"muted small\">").Append(E(string.Format(c, l.MoreRules, group.Count() - max))).Append("</p>");
            }
        }

        sb.Append("</section>");
    }

    private static void RenderModernization(StringBuilder sb, ReportModernization? m, ReportLocale l, CultureInfo c)
    {
        sb.Append("<section class=\"modernization\"><h2>").Append(E(l.ModernizationTitle)).Append("</h2>");
        if (m is null)
        {
            sb.Append("<p class=\"muted\">").Append(E(l.NoModernization)).Append("</p></section>");
            return;
        }

        var e = m.RecommendedEstimate;
        sb.Append("<div class=\"health-hero\"><div><p class=\"eyebrow\">").Append(E(l.RecommendedStrategy)).Append("</p><h3 class=\"risk-label\">").Append(E(m.RecommendedName)).Append("</h3>")
          .Append("<p class=\"muted\">").Append(E(m.RecommendedDescription)).Append("</p></div></div>");

        sb.Append("<div class=\"tiles\">");
        Tile(sb, l.EffortRange, $"{Hours(e.OptimisticHours, c)} – {Hours(e.ConservativeHours, c)} {l.Hours}", null);
        Tile(sb, l.DurationRange, $"{e.OptimisticMonths.ToString("0.#", c)} – {e.ConservativeMonths.ToString("0.#", c)} {l.Months}", null);
        Tile(sb, l.CostRange, $"{Money(e.OptimisticCost, e.Currency, c)} – {Money(e.ConservativeCost, e.Currency, c)}", null);
        Tile(sb, l.EstimateConfidence, e.Confidence, null);
        if (m.Savings is { AnnualTotal: > 0 } sv)
        {
            Tile(sb, l.AnnualSavings, Money(sv.AnnualTotal, sv.Currency, c), null);
            if (sv.RecommendedPaybackMonths is { } payback)
            {
                Tile(sb, l.Payback, $"{payback.ToString("0.#", c)} {l.Months}", null);
            }
        }

        sb.Append("</div>");

        sb.Append("<table class=\"strategies\"><thead><tr><th>").Append(E(l.Strategy)).Append("</th><th class=\"num\">").Append(E(l.Fit)).Append("</th><th>").Append(E(l.Risk))
          .Append("</th><th class=\"num\">").Append(E(l.EffortLikely)).Append("</th><th class=\"num\">").Append(E(l.DurationLikely)).Append("</th><th class=\"num\">").Append(E(l.CostLikely))
          .Append("</th><th class=\"num\">").Append(E(l.Payback)).Append("</th></tr></thead>");
        foreach (var s in m.Strategies)
        {
            // One tbody per strategy keeps the numbers row and its rationale together across page breaks.
            sb.Append(s.Recommended ? "<tbody class=\"strategy recommended\">" : "<tbody class=\"strategy\">")
              .Append("<tr><td><strong>").Append(E(s.Name)).Append("</strong>").Append(s.Recommended ? " ★" : string.Empty).Append("</td>")
              .Append("<td class=\"num\">").Append(s.FitScore.ToString(c)).Append("</td>")
              .Append("<td>").Append(RiskChip(s.Risk, l)).Append("</td>")
              .Append("<td class=\"num\">").Append(Hours(s.LikelyHours, c)).Append(' ').Append(E(l.Hours)).Append("</td>")
              .Append("<td class=\"num\">").Append(s.LikelyMonths.ToString("0.#", c)).Append(' ').Append(E(l.Months)).Append("</td>")
              .Append("<td class=\"num\">").Append(E(Money(s.LikelyCost, s.Currency, c))).Append("</td>")
              .Append("<td class=\"num\">").Append(s.PaybackMonths is { } pb ? E($"{pb.ToString("0.#", c)} {l.Months}") : "—").Append("</td></tr>");
            var reasons = s.Rationale.Concat(s.Blockers.Select(b => "⚠ " + b)).ToList();
            if (reasons.Count > 0)
            {
                sb.Append("<tr class=\"why\"><td colspan=\"7\"><span class=\"eyebrow\">").Append(E(l.Why)).Append("</span> ")
                  .Append(string.Join(" · ", reasons.Select(E))).Append("</td></tr>");
            }

            sb.Append("</tbody>");
        }

        sb.Append("</table>");

        if (m.Savings is { AnnualTotal: > 0 } savings)
        {
            sb.Append("<h3>").Append(E(l.AnnualSavings)).Append("</h3><table class=\"dense\"><tbody>");
            foreach (var (label, amount) in savings.Items)
            {
                sb.Append("<tr><td>").Append(E(label)).Append("</td><td class=\"num\">").Append(E(Money(amount, savings.Currency, c))).Append("</td></tr>");
            }

            sb.Append("<tr><td><strong>Σ</strong></td><td class=\"num\"><strong>").Append(E(Money(savings.AnnualTotal, savings.Currency, c))).Append("</strong></td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<h3>").Append(E(l.Breakdown)).Append("</h3><table class=\"dense\"><thead><tr><th></th><th class=\"num\">").Append(E(l.Quantity)).Append("</th><th class=\"num\">").Append(E(l.Likely)).Append("</th></tr></thead><tbody>");
        foreach (var (label, hours, quantity) in e.Breakdown)
        {
            sb.Append("<tr><td>").Append(E(label)).Append("</td><td class=\"num\">").Append(quantity.ToString("0.#", c)).Append("</td><td class=\"num\">").Append(Hours(hours, c)).Append(' ').Append(E(l.Hours)).Append("</td></tr>");
        }

        sb.Append("</tbody></table>");

        sb.Append("<h3>").Append(E(l.Assumptions)).Append("</h3><table class=\"kv dense\">");
        foreach (var (label, value) in e.Assumptions)
        {
            sb.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
        }

        sb.Append("</table>");

        sb.Append("<h3>").Append(E(l.RoadmapTitle)).Append("</h3><table><thead><tr><th>").Append(E(l.Phase)).Append("</th><th class=\"num\">").Append(E(l.Share))
          .Append("</th><th class=\"num\">").Append(E(l.EffortLikely)).Append("</th><th class=\"num\">").Append(E(l.DurationLikely)).Append("</th><th>").Append(E(l.DependsOn)).Append("</th><th>").Append(E(l.WorkItems)).Append("</th></tr></thead><tbody>");
        foreach (var ph in m.Phases)
        {
            sb.Append("<tr><td><strong>").Append(E(ph.Name)).Append("</strong></td>")
              .Append("<td class=\"num\">").Append((ph.Share * 100).ToString("0", c)).Append("%</td>")
              .Append("<td class=\"num\">").Append(Hours(ph.LikelyHours, c)).Append(' ').Append(E(l.Hours)).Append("</td>")
              .Append("<td class=\"num\">").Append(ph.LikelyMonths.ToString("0.#", c)).Append(' ').Append(E(l.Months)).Append("</td>")
              .Append("<td class=\"small\">").Append(E(ph.DependsOn.Count == 0 ? "—" : string.Join(", ", ph.DependsOn))).Append("</td>")
              .Append("<td class=\"small\">").Append(string.Join("<br>", ph.WorkItems.Select(w => E(w.Quantity > 1 ? $"{w.Label} ×{w.Quantity.ToString("N0", c)}" : w.Label)))).Append("</td></tr>");
        }

        sb.Append("</tbody></table>");
        sb.Append("<aside class=\"note\">").Append(E(l.ModernizationNote)).Append(" <span class=\"mono small\">").Append(E(m.ModelVersions)).Append("</span></aside></section>");
    }

    private static string Hours(double hours, CultureInfo c) => hours.ToString("N0", c);

    private static string Money(decimal amount, string currency, CultureInfo c) =>
        currency == "BRL" ? "R$ " + amount.ToString("N0", c) : $"{amount.ToString("N0", c)} {currency}";

    private static string RiskChip(string risk, ReportLocale l) =>
        $"<span class=\"chip risk-chip-{E(risk.ToLowerInvariant())}\">{E(l.Term(risk))}</span>";

    private static void RenderInventory(StringBuilder sb, ExecutiveReport r, ReportLocale l, CultureInfo c)
    {
        sb.Append("<section><h2>").Append(E(l.Inventory)).Append("</h2>");
        foreach (var i in r.Inventory)
        {
            sb.Append("<h3>").Append(E(i.LanguageId)).Append(" · ").Append(i.Projects.Count.ToString("N0", c)).Append(' ').Append(E(l.Projects.ToLower(c))).Append(" · ")
              .Append(i.Solutions.ToString("N0", c)).Append(' ').Append(E(l.Solutions)).Append("</h3>");
            sb.Append("<table><thead><tr><th>").Append(E(l.Project)).Append("</th><th>").Append(E(l.Format)).Append("</th><th>").Append(E(l.TargetFramework))
              .Append("</th><th class=\"num\">").Append(E(l.Packages)).Append("</th><th class=\"num\">packages.config</th><th class=\"num\">").Append(E(l.ProjectRefs)).Append("</th></tr></thead><tbody>");
            foreach (var p in i.Projects)
            {
                sb.Append("<tr><td>").Append(E(p.Name)).Append("<br><span class=\"mono small muted\">").Append(E(p.Path)).Append("</span></td>")
                  .Append("<td>").Append(E(p.IsSdkStyle ? l.SdkStyle : l.Legacy)).Append("</td>")
                  .Append("<td class=\"mono\">").Append(E(p.TargetFramework ?? "—")).Append("</td>")
                  .Append("<td class=\"num\">").Append(p.PackageCount).Append("</td>")
                  .Append("<td class=\"num\">").Append(p.PackagesConfigCount).Append("</td>")
                  .Append("<td class=\"num\">").Append(p.ProjectReferenceCount).Append("</td></tr>");
            }

            sb.Append("</tbody></table>");
            sb.Append("<p class=\"muted small\">").Append(E(string.Format(c, l.Complexity, i.MaxComplexity, i.AverageComplexity.ToString("F1", c)))).Append("</p>");
        }

        sb.Append("</section>");
    }

    private static void RenderAppendix(StringBuilder sb, ExecutiveReport r, ReportLocale l)
    {
        sb.Append("<section class=\"appendix\"><h2>").Append(E(l.AppendixFindings)).Append("</h2>");
        var shown = r.Findings.OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId, StringComparer.Ordinal).ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).Take(MaxAppendixRows).ToList();
        if (r.Findings.Count > shown.Count)
        {
            sb.Append("<p class=\"muted small\">").Append(E(string.Format(l.Culture, l.AppendixCapped, shown.Count, r.Findings.Count))).Append("</p>");
        }

        sb.Append("<table class=\"dense findings\"><colgroup><col class=\"c-sev\"><col class=\"c-status\"><col class=\"c-rule\"><col><col class=\"c-conf\"></colgroup><thead><tr><th>").Append(E(l.Severity)).Append("</th><th>").Append(E(l.StatusColumn)).Append("</th><th>").Append(E(l.Rule))
          .Append("</th><th>").Append(E(l.Title)).Append(" / ").Append(E(l.Location)).Append("</th><th>").Append(E(l.Confidence)).Append("</th></tr></thead><tbody>");
        foreach (var f in shown)
        {
            var location = f.FilePath is null ? f.Symbol ?? string.Empty : f.Line is null ? f.FilePath : $"{f.FilePath}:{f.Line}";
            sb.Append("<tr><td>").Append(SeverityChip(f.Severity, l)).Append("</td><td>").Append(E(l.Term(f.Status.ToString()))).Append("</td>")
              .Append("<td class=\"mono small\">").Append(E(f.RuleId)).Append("</td>")
              .Append("<td>").Append(E(f.Title)).Append("<br><span class=\"mono small muted\">").Append(E(location)).Append("</span></td>")
              .Append("<td>").Append(E(f.Confidence is null ? "—" : l.Term(f.Confidence))).Append("</td></tr>");
        }

        sb.Append("</tbody></table>");

        sb.Append("<h2>").Append(E(l.AppendixMethodology)).Append("</h2><ul class=\"method\">");
        foreach (var item in l.MethodologyItems)
        {
            sb.Append("<li>").Append(E(item)).Append("</li>");
        }

        sb.Append("</ul></section>");
    }

    private static void Tile(StringBuilder sb, string label, string value, string? tone)
    {
        sb.Append("<div class=\"tile").Append(tone is null ? string.Empty : " tone-" + tone).Append("\"><span class=\"tile-v\">")
          .Append(E(value)).Append("</span><span class=\"tile-l\">").Append(E(label)).Append("</span></div>");
    }

    private static string SeverityChip(Severity severity, ReportLocale l) =>
        $"<span class=\"chip sev-{severity.ToString().ToLowerInvariant()}\">{E(l.Term(severity.ToString()))}</span>";

    private static string StatusChip(string status, ReportLocale l) =>
        $"<span class=\"chip st-{E(status.ToLowerInvariant())}\">{E(l.Term(status))}</span>";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // Encodes only HTML-significant characters; UTF-8 text (accents, arrows) passes through intact.
    private static readonly System.Text.Encodings.Web.HtmlEncoder Encoder =
        System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

    private static string E(string? value) => Encoder.Encode(value ?? string.Empty);

    private const string Css = """
        :root{--bg:#F7F8F6;--surface:#FFFFFF;--ink:#212930;--soft:#55616A;--line:#D9DEDA;--accent:#1F6E68;
              --crit:#8E2F1F;--high:#A8432E;--med:#9A6A0B;--low:#4E6E8E;--info:#6B7680;--ok:#2E7D4F;--warn:#B26A00;--fail:#A8432E}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.55 "Charter","Bitstream Charter","Sitka Text",Cambria,Georgia,serif}
        .page{max-width:62rem;margin:0 auto;padding:2.5rem 1.5rem 4rem}
        .eyebrow{font:600 .72rem/1 ui-monospace,Consolas,monospace;letter-spacing:.14em;text-transform:uppercase;color:var(--accent);margin:0 0 .6rem}
        h1{font-size:2rem;line-height:1.15;margin:0 0 1rem;letter-spacing:-.01em}
        h2{font-size:1.35rem;margin:2.6rem 0 .8rem;padding-top:1.2rem;border-top:1px solid var(--line)}
        h3{font-size:1.05rem;margin:1.4rem 0 .5rem}
        .meta{display:grid;grid-template-columns:auto 1fr;gap:.2rem 1rem;margin:0;font-size:.92rem}
        .meta dt{color:var(--soft)}.meta dd{margin:0;overflow-wrap:anywhere}
        .mono{font-family:ui-monospace,"Cascadia Code",Consolas,monospace;font-size:.88em}.small{font-size:.82rem}.muted{color:var(--soft)}
        .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(9.5rem,1fr));gap:.7rem;margin:0 0 1rem}
        .tile{background:var(--surface);border:1px solid var(--line);border-radius:6px;padding:.9rem 1rem;display:flex;flex-direction:column;gap:.2rem}
        .tile-v{font-size:1.7rem;font-weight:700;font-variant-numeric:tabular-nums;line-height:1.1}.tile-l{font-size:.78rem;color:var(--soft);text-transform:uppercase;letter-spacing:.06em}
        .tone-critical .tile-v{color:var(--high)}.tone-medium .tile-v{color:var(--med)}.tone-low .tile-v{color:var(--low)}
        table{width:100%;border-collapse:collapse;background:var(--surface);border:1px solid var(--line);border-radius:6px;margin:0 0 1rem;font-size:.9rem}
        th{text-align:left;font:600 .7rem/1.2 ui-monospace,Consolas,monospace;letter-spacing:.1em;text-transform:uppercase;color:var(--soft);padding:.55rem .7rem;border-bottom:2px solid var(--line)}
        td{padding:.55rem .7rem;border-bottom:1px solid var(--line);vertical-align:top;overflow-wrap:anywhere}tr:last-child td{border-bottom:none}
        .num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}.dense td,.dense th{padding:.35rem .5rem;font-size:.82rem}
        .kv th{width:9rem;border-bottom:none}.kv td{border-bottom:none}
        .note{background:#EEF3F1;border-left:4px solid var(--accent);padding:.8rem 1rem;border-radius:4px;font-size:.92rem;margin:1rem 0 0}
        .chip{display:inline-block;padding:.1rem .5rem;border-radius:999px;font:600 .7rem/1.5 ui-monospace,Consolas,monospace;letter-spacing:.05em;text-transform:uppercase;color:#fff;white-space:nowrap}
        .sev-critical{background:var(--crit)}.sev-high{background:var(--high)}.sev-medium{background:var(--med)}.sev-low{background:var(--low)}.sev-informational{background:var(--info)}
        .st-succeeded{background:var(--ok)}.st-failed{background:var(--fail)}.st-running,.st-cancelled{background:var(--warn)}
        .risks{padding-left:1.4rem}.risks li{margin:0 0 .8rem}.rem{font-size:.88rem;color:var(--soft);margin:.15rem 0 0}
        .health-hero{display:flex;gap:1.4rem;align-items:center;background:var(--surface);border:1px solid var(--line);border-radius:6px;padding:1rem 1.2rem;margin:0 0 1rem}
        .score{display:flex;align-items:baseline;gap:.1rem;font-variant-numeric:tabular-nums}.score-v{font-size:3rem;font-weight:700;line-height:1}.score-d{font-size:1rem;color:var(--soft)}
        .risk-label{margin:0 0 .3rem;font-size:1.05rem}.risk-critical{color:var(--crit)}.risk-high{color:var(--high)}.risk-medium{color:var(--med)}.risk-low{color:var(--ok)}
        .bar{width:9rem;height:.55rem;background:#E4E9E6;border-radius:999px;overflow:hidden}.bar-fill{height:100%;border-radius:999px}
        .bar-critical{background:var(--crit)}.bar-high{background:var(--high)}.bar-medium{background:var(--med)}.bar-low{background:var(--ok)}
        .method{font-size:.9rem}.method li{margin:0 0 .4rem}
        .logo{max-height:48px;max-width:240px;margin:0 0 .8rem;display:block}.verdict{font-size:1.15rem;line-height:1.5;margin:0 0 1rem}.since{margin:.4rem 0}.compare{display:grid;grid-template-columns:1fr 1fr;gap:1rem}.risks.compact li{margin:0 0 .3rem}.pagebreak{break-after:page}
        tr.recommended td{background:#EEF3F1}.risk-chip-low{background:var(--ok)}.risk-chip-medium{background:var(--med)}.risk-chip-high{background:var(--high)}.risk-chip-critical{background:var(--crit)}
        @media print{body{background:#fff;font-size:11pt}.page{max-width:none;padding:0}h2{break-after:avoid}table{break-inside:auto}tr{break-inside:avoid}.tile{break-inside:avoid}}
        .ai-summary{border-left:4px solid var(--accent);padding-left:14px;margin:18px 0}.ai-summary p{margin:6px 0}.ai-label{font-style:italic}
        .ai-plan{border-left:4px solid var(--accent);padding-left:14px;margin:18px 0;break-before:page}.ai-plan h3{margin:14px 0 4px}.ai-plan h4{margin:10px 0 2px;font-size:.95rem}.ai-plan p{margin:6px 0}.ai-plan ul,.ai-plan ol{margin:4px 0 8px 18px}.ai-plan li{margin:2px 0}.ai-plan code{font-size:.9em}.business-rules h3{margin:14px 0 4px}
        .strategies tbody.strategy{break-inside:avoid}.strategies tr.why td{padding-top:0;border-bottom:1px solid var(--line);font-size:.82rem;color:var(--soft)}.strategies tbody.strategy tr:first-child td{border-bottom:none}.strategies tbody.recommended td{background:#EEF3F1}
        .findings{table-layout:fixed}.findings .c-sev{width:5.8rem}.findings .c-status{width:5rem}.findings .c-rule{width:9rem}.findings .c-conf{width:5.6rem}.findings th{letter-spacing:.04em;white-space:normal;overflow-wrap:anywhere}.findings td{overflow-wrap:anywhere;word-break:normal}
        """;

    /// <summary>Chromium print footer: brand, assessment, confidentiality note and page X of Y.</summary>
    public static string RenderPdfFooter(ExecutiveReport report, ReportLocale? locale = null)
    {
        var l = locale ?? ReportLocale.En;
        return "<html><head><style>body{margin:0;font:9px ui-sans-serif,system-ui,sans-serif;color:#55616A}.f{display:flex;justify-content:space-between;width:100%;box-sizing:border-box;padding:0 0.5in}</style></head><body>"
            + "<div class=\"f\"><span>" + E(report.Header.BrandName) + " · " + E(report.Header.AssessmentName) + " · " + E(l.Confidential) + "</span>"
            + "<span>" + E(l.PageOf).Replace("{0}", "<span class=\"pageNumber\"></span>").Replace("{1}", "<span class=\"totalPages\"></span>") + "</span></div></body></html>";
    }
}
