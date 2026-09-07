using System.Globalization;
using System.Text;
using Atlas.Application.Ai;
using Atlas.Application.Assessments;

namespace Atlas.Reporting;

/// <summary>
/// Turns the deterministic report into a fact sheet and asks the model for the
/// executive summary. The model only sees numbers Atlas computed; the summary
/// is stored and shown on page one labelled as AI-generated.
/// </summary>
public sealed class ReportNarrativeService(ExecutiveReportBuilder reports, ModernizationPlanBuilder plans, AiNarrativeService narratives)
{
    public async Task<NarrativeResult> GenerateSummaryAsync(Guid assessmentId, string? lang, CancellationToken cancellationToken)
    {
        var locale = ReportLocale.For(lang);
        var report = await reports.BuildAsync(assessmentId, locale, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        return await narratives.WriteSummaryAsync(assessmentId, lang, Facts(report, locale), cancellationToken);
    }

    /// <summary>
    /// Drafts the migration plan for the recommended strategy: the report's headline
    /// facts plus the full modernization plan (profile, strategy rationale, estimate
    /// with assumptions, roadmap phases and work items) go to the model as text.
    /// </summary>
    public async Task<NarrativeResult> GenerateMigrationPlanAsync(Guid assessmentId, string? lang, CancellationToken cancellationToken)
    {
        var locale = ReportLocale.For(lang);
        var report = await reports.BuildAsync(assessmentId, locale, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        var plan = await plans.BuildAsync(assessmentId, cancellationToken)
            ?? throw new InvalidOperationException("No inventory yet: run the assessment before drafting a migration plan.");
        return await narratives.WriteMigrationPlanAsync(assessmentId, lang, PlanFacts(report, plan, locale), cancellationToken);
    }

    /// <summary>The summary facts followed by everything the modernization engines computed, one fact per line. Labels in the report's language, numbers exact.</summary>
    public static string PlanFacts(ExecutiveReport r, ModernizationPlan plan, ReportLocale l)
    {
        var lang = l.Code;
        var c = l.Culture;
        var p = plan.Profile;
        var sb = new StringBuilder(Facts(r, l));

        sb.AppendLine();
        sb.AppendLine("== Estate profile ==");
        sb.AppendLine($"Lines of code: {p.LinesOfCode.ToString("N0", c)}; projects: {p.Projects} (legacy framework {p.LegacyFrameworkProjects}, modern {p.ModernFrameworkProjects}, unknown {p.UnknownFrameworkProjects}; legacy project format {p.LegacyProjectFormat}); types: {p.Types}; methods: {p.Methods}; cyclomatic complexity max {p.MaxComplexity}, average {p.AverageComplexity.ToString("0.0", c)}.");
        if (p.UiFrameworks is { Count: > 0 } ui)
        {
            sb.AppendLine($"UI/hosting frameworks: {string.Join(", ", ui.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}"))}; projects with no upgrade path onto modern .NET: {p.NoUpgradePathProjects}; desktop projects: {p.DesktopProjects}.");
        }

        sb.AppendLine($"Migration blockers: prerequisite {p.PrerequisiteBlockers}, high {p.HighBlockers}, medium {p.MediumBlockers}; projects with blockers {p.ProjectsWithBlockers} of {p.Projects}.");
        foreach (var b in p.Blockers)
        {
            var title = r.RuleGroups.FirstOrDefault(g => g.RuleId == b.RuleId)?.Title ?? b.RuleId;
            sb.AppendLine($"Blocker {b.RuleId} ({b.Weight}): {title} — {b.Occurrences} occurrence(s).");
        }

        sb.AppendLine($"Security debt: critical {p.CriticalSecurity}, high {p.HighSecurity}, medium {p.MediumSecurity}; secrets found {p.SecretsFound}; vulnerable packages {p.VulnerablePackages}.");
        sb.AppendLine($"Tests: {(p.HasTests ? "present" : "none found")}; line coverage {(p.CoverageLineRate is { } cr ? cr.ToString("P0", c) : "unknown")}; projects without tests {p.ProjectsWithoutTests}.");
        sb.AppendLine($"Architecture: dependency cycles {p.ArchitectureCycles}; high fan-out types {p.HighFanOut}; web UI {(p.HasWebUi ? "yes" : "no")}; Entity Framework 6 {(p.HasEntityFramework6 ? "yes" : "no")}; WCF/Remoting/MSMQ {(p.HasWcfRemotingOrMsmq ? "yes" : "no")}; analysis tier {p.Tier ?? "unknown"}{(p.SymbolResolutionRate is { } sr ? $", symbol resolution {sr.ToString("P0", c)}" : "")}.");

        sb.AppendLine();
        sb.AppendLine($"== Strategy comparison ({plan.Assessment.ModelVersion}) ==");
        foreach (var s in plan.Assessment.Strategies.OrderByDescending(s => s.FitScore))
        {
            var est = plan.Estimates.First(e => e.Strategy == s.Strategy);
            var recommended = s.Strategy == plan.Assessment.Recommended;
            sb.AppendLine($"{(recommended ? "RECOMMENDED" : "Alternative")}: {ModernizationTexts.Strategy(s.Strategy, lang)} — fit {s.FitScore}/100, risk {s.Risk}, likely {est.EffortHours.Likely.ToString("N0", c)} h / {est.DurationMonths.Likely.ToString("0.#", c)} months / {est.Cost.Likely.ToString("N0", c)} {est.Cost.Currency}.");
            if (recommended)
            {
                sb.AppendLine($"  Description: {ModernizationTexts.StrategyDescription(s.Strategy, lang)}");
                AppendList(sb, "  Why", s.Rationale, lang);
                AppendList(sb, "  Prerequisites", s.Prerequisites, lang);
                AppendList(sb, "  Blockers", s.Blockers, lang);
                AppendList(sb, "  Benefits", s.Benefits, lang);
            }
        }

        var rec = plan.Recommended;
        sb.AppendLine();
        sb.AppendLine($"== Estimate for the recommended strategy ({rec.ModelVersion}) ==");
        sb.AppendLine($"Effort {rec.EffortHours.Optimistic.ToString("N0", c)}–{rec.EffortHours.Conservative.ToString("N0", c)} hours (likely {rec.EffortHours.Likely.ToString("N0", c)}); duration {rec.DurationMonths.Optimistic.ToString("0.#", c)}–{rec.DurationMonths.Conservative.ToString("0.#", c)} months (likely {rec.DurationMonths.Likely.ToString("0.#", c)}); cost {rec.Cost.Optimistic.ToString("N0", c)}–{rec.Cost.Conservative.ToString("N0", c)} {rec.Cost.Currency} (likely {rec.Cost.Likely.ToString("N0", c)}); confidence {ModernizationTexts.Confidence(rec.Confidence, lang)}.");
        foreach (var b in rec.Breakdown)
        {
            sb.AppendLine($"Effort item: {ModernizationTexts.Text(b.Key, lang)} — {Math.Round(b.Hours).ToString("N0", c)} h (quantity {b.Quantity.ToString("0.##", c)}).");
        }

        foreach (var a in rec.Assumptions)
        {
            sb.AppendLine($"Assumption: {ModernizationTexts.Text(a.Key, lang)} = {(a.Key == "assumption.confidence" ? ModernizationTexts.Confidence(rec.Confidence, lang) : a.Value)}.");
        }

        sb.AppendLine();
        sb.AppendLine($"== Roadmap ({plan.Roadmap.ModelVersion}; phases run sequentially with the same team, no parallelism credit) ==");
        foreach (var ph in plan.Roadmap.Phases)
        {
            var deps = ph.DependsOn.Count == 0 ? "nothing" : string.Join(", ", ph.DependsOn.Select(d => ModernizationTexts.Text(d, lang)));
            var items = string.Join("; ", ph.WorkItems.Select(w => w.Quantity > 1 ? $"{ModernizationTexts.Text(w.Key, lang)} ×{w.Quantity}" : ModernizationTexts.Text(w.Key, lang)));
            sb.AppendLine($"Phase {ph.Order + 1}: {ModernizationTexts.Text(ph.Key, lang)} — {ph.EffortShare.ToString("P0", c)} of effort, {ph.EffortHours.Optimistic.ToString("N0", c)}–{ph.EffortHours.Conservative.ToString("N0", c)} h (likely {ph.EffortHours.Likely.ToString("N0", c)}), {ph.DurationMonths.Optimistic.ToString("0.#", c)}–{ph.DurationMonths.Conservative.ToString("0.#", c)} months; depends on: {deps}; work items: {items}.");
        }

        if (r.BusinessRules is { Count: > 0 } rules)
        {
            sb.AppendLine();
            sb.AppendLine("== Business rules recovered by AI (knowledge to preserve through the migration; sample) ==");
            foreach (var rule in rules.OrderByDescending(x => x.Confidence).Take(12))
            {
                sb.AppendLine($"Rule: {rule.Name} ({rule.Category}) in {rule.Symbol}.");
            }
        }

        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> keys, string lang)
    {
        if (keys.Count > 0)
        {
            sb.AppendLine($"{label}: {string.Join("; ", keys.Select(k => ModernizationTexts.Text(k, lang)))}.");
        }
    }

    /// <summary>Plain-text facts, one per line, in the report's language where labels matter.</summary>
    public static string Facts(ExecutiveReport r, ReportLocale l)
    {
        var c = l.Culture;
        var sb = new StringBuilder();
        sb.AppendLine($"Assessment: {r.Header.AssessmentName}; source kind: {r.Header.SourceKind}; status: {r.Header.Status}.");
        if (r.Health is not null)
        {
            sb.AppendLine($"Health score: {r.Health.Score}/100; risk level: {r.Health.RiskLevel}.");
            foreach (var d in r.Health.Dimensions)
            {
                sb.AppendLine($"Dimension {d.Name}: score {d.Score}/100 (weight {d.Weight.ToString("0.##", CultureInfo.InvariantCulture)}); main contributors: {string.Join("; ", d.Contributors.Take(3))}.");
            }
        }

        sb.AppendLine($"Open findings: {r.Totals.Open}; resolved: {r.Totals.Resolved}; suppressed: {r.Totals.Suppressed}.");
        sb.AppendLine("Open by severity: " + string.Join(", ", r.Totals.OpenBySeverity.OrderByDescending(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")) + ".");
        sb.AppendLine("Open by category: " + string.Join(", ", r.Totals.OpenByCategory.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")) + ".");
        foreach (var g in r.RuleGroups.OrderByDescending(g => g.MaxSeverity).ThenByDescending(g => g.OpenCount).Take(8))
        {
            sb.AppendLine($"Top rule: {g.Title} — {g.OpenCount} open, max severity {g.MaxSeverity}.");
        }

        if (r.Inventory.Count > 0)
        {
            var frameworks = r.Inventory.SelectMany(i => i.Projects.Select(p => p.TargetFramework)).Where(f => f is not null).GroupBy(f => f!).OrderByDescending(g => g.Count()).Take(6);
            sb.AppendLine($"Projects: {r.Inventory.Sum(i => i.Projects.Count)}; source files: {r.Inventory.Sum(i => i.Files)}; target frameworks: {string.Join(", ", frameworks.Select(g => $"{g.Key} ×{g.Count()}"))}.");
        }

        if (r.Modernization is { } m)
        {
            var e = m.RecommendedEstimate;
            sb.AppendLine($"Recommended modernization strategy: {m.RecommendedName}; effort {e.OptimisticHours.ToString("N0", c)}–{e.ConservativeHours.ToString("N0", c)} hours (likely {e.LikelyHours.ToString("N0", c)}), {e.LikelyMonths.ToString("0.#", c)} months, likely cost {e.LikelyCost.ToString("N0", c)} {e.Currency}, confidence {e.Confidence}.");
            foreach (var s in m.Strategies.Where(s => !s.Recommended).Take(2))
            {
                sb.AppendLine($"Alternative strategy: {s.Name} (fit {s.FitScore}, risk {s.Risk}).");
            }
        }

        if (r.Comparison is { } cmp)
        {
            sb.AppendLine($"Since run #{cmp.PreviousRun}: {cmp.Resolved} resolved, {cmp.New} new, {cmp.Regressed} regressed; health delta {cmp.HealthDelta?.ToString("+0;-0;0") ?? "n/a"}.");
        }

        if (r.BusinessRules is { Count: > 0 } rules)
        {
            sb.AppendLine($"Business rules recovered by AI: {rules.Count} (categories: {string.Join(", ", rules.GroupBy(x => x.Category).Select(g => $"{g.Key} {g.Count()}"))}).");
        }

        return sb.ToString();
    }
}
