using System.Globalization;
using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Domain.Findings;

namespace Atlas.Reporting;

/// <summary>
/// Assembles the executive report from persisted facts only — no re-analysis,
/// no AI. Every number traces back to findings, scans and inventory snapshots;
/// every word is rendered in the requested locale from rule localizations.
/// </summary>
public sealed class ExecutiveReportBuilder(
    IAssessmentRepository assessments,
    IScanRepository scans,
    IFindingRepository findings,
    IInventoryRepository inventory,
    IRuleCatalog rules,
    IHealthRepository health,
    ReportOptions options,
    ModernizationPlanBuilder planBuilder,
    IAssessmentRunRepository runs,
    RunComparisonBuilder comparisons,
    Atlas.Application.Ai.IBusinessRuleRepository businessRules,
    Atlas.Application.Ai.IAiNarrativeRepository narratives)
{
    private const int MaxFindings = 5000;
    private const int SampleLocations = 5;

    public Task<ExecutiveReport?> BuildAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        BuildAsync(assessmentId, ReportLocale.En, cancellationToken);

    public async Task<ExecutiveReport?> BuildAsync(Guid assessmentId, ReportLocale locale, CancellationToken cancellationToken)
    {
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken);
        if (assessment is null)
        {
            return null;
        }

        var lang = locale.Code;
        var scanList = await scans.ListByAssessmentAsync(assessmentId, cancellationToken);
        var snapshots = await inventory.GetLatestByAssessmentAsync(assessmentId, cancellationToken);
        var page = await findings.ListAsync(assessmentId, 0, MaxFindings, cancellationToken);
        var catalog = await rules.GetAllAsync(cancellationToken);
        var healthSnapshot = await health.GetLatestAsync(assessmentId, cancellationToken);

        ReportHealth? reportHealth = null;
        if (healthSnapshot is not null)
        {
            reportHealth = new ReportHealth(
                healthSnapshot.Score,
                healthSnapshot.RiskLevel.ToString(),
                healthSnapshot.ModelVersion,
                string.Format(locale.Culture, locale.HealthExplanation,
                    healthSnapshot.OpenFindings, healthSnapshot.ProjectCount, healthSnapshot.ModelVersion),
                HealthSnapshotFactory.ReadDimensions(healthSnapshot)
                    .Select(d => new ReportHealthDimension(
                        d.Name, d.Weight, d.Score, d.Penalty,
                        d.Contributors.Select(c =>
                            $"{FindingLocalizer.RuleTitle(catalog.GetValueOrDefault(c.RuleId), c.RuleId, lang)} ×{c.Count} (−{c.Points.ToString("0.#", CultureInfo.InvariantCulture)})").ToList()))
                    .ToList());
        }

        var header = new ReportHeader(
            options.BrandName,
            options.PreparedBy,
            assessment.Name,
            assessment.SourceKind,
            assessment.SourceLocator,
            assessment.Branch,
            snapshots.FirstOrDefault()?.CommitSha ?? scanList.FirstOrDefault(s => s.CommitSha is not null)?.CommitSha,
            assessment.Status.ToString(),
            DateTimeOffset.UtcNow,
            assessment.CompletedAtUtc);

        // Latest scan per scanner tells the coverage story; older runs are history.
        var latestScans = scanList
            .GroupBy(s => s.ScannerId)
            .Select(g => g.OrderByDescending(s => s.StartedAtUtc).First())
            .OrderBy(s => s.ScannerId)
            .Select(s => new ReportScan(
                s.ScannerId, s.ScannerVersion, s.Status.ToString(), s.FindingsEmitted, s.FindingsNew,
                s.FindingsRecurring, s.FindingsResolved, s.FindingsRegressed, s.Error))
            .ToList();

        var inventories = snapshots
            .Select(s => new ReportInventory(
                s.LanguageId, s.TierAchieved, s.FileCount, s.TotalLines, s.TypeCount, s.MethodCount,
                s.MaxCyclomaticComplexity, s.AverageCyclomaticComplexity, s.SymbolResolutionRate,
                s.SolutionCount, InventorySnapshotFactory.ReadProjects(s)))
            .ToList();

        var reportFindings = page.Items
            .Select(i =>
            {
                var text = FindingLocalizer.Localize(i.Finding, i.Latest, catalog.GetValueOrDefault(i.Finding.RuleId), lang);
                return new ReportFinding(
                    i.Finding.RuleId, text.Title, i.Finding.Category, i.Finding.Severity, i.Finding.Status,
                    i.Latest?.Confidence.ToString(), i.Latest?.Evidence.FilePath, i.Latest?.Evidence.LineStart,
                    i.Latest?.Evidence.Symbol, text.Message);
            })
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.RuleId)
            .ThenBy(f => f.FilePath)
            .ToList();

        var open = reportFindings.Where(f => f.Status is FindingStatus.Open or FindingStatus.Regressed).ToList();

        var totals = new ReportTotals(
            Open: open.Count,
            Resolved: reportFindings.Count(f => f.Status == FindingStatus.Resolved),
            Suppressed: reportFindings.Count(f => f.Status is FindingStatus.Suppressed or FindingStatus.FalsePositive),
            OpenBySeverity: Enum.GetValues<Severity>().ToDictionary(s => s, s => open.Count(f => f.Severity == s)),
            OpenByCategory: Enum.GetValues<FindingCategory>().ToDictionary(c => c, c => open.Count(f => f.Category == c)));

        var groups = open
            .GroupBy(f => f.RuleId)
            .Select(g =>
            {
                var rule = catalog.GetValueOrDefault(g.Key);
                return new ReportRuleGroup(
                    RuleId: g.Key,
                    Title: FindingLocalizer.RuleTitle(rule, g.Key, lang),
                    Category: g.First().Category,
                    MaxSeverity: g.Max(f => f.Severity),
                    OpenCount: g.Count(),
                    Remediation: FindingLocalizer.RuleRemediation(rule, lang),
                    SampleLocations: g
                        .Select(f => f.FilePath is null ? f.Symbol ?? string.Empty : f.Line is null ? f.FilePath : $"{f.FilePath}:{f.Line}")
                        .Where(l => l.Length > 0)
                        .Distinct()
                        .Take(SampleLocations)
                        .ToList());
            })
            .OrderByDescending(g => g.MaxSeverity)
            .ThenByDescending(g => g.OpenCount)
            .ToList();

        var modernization = await BuildModernizationAsync(assessmentId, lang, cancellationToken);
        var comparison = await BuildComparisonAsync(assessmentId, lang, cancellationToken);
        var projectRows = BuildProjectRows(inventories, open);
        var verdict = BuildVerdict(locale, assessment.Name, reportHealth, totals, modernization, projectRows);
        var pt = lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var recoveredRules = (await businessRules.ListAsync(assessmentId, cancellationToken))
            .Select(r => new ReportBusinessRule(r.FilePath, r.Symbol, r.StartLine, r.Name, pt ? r.DescriptionPt : r.DescriptionEn, r.Category.ToString(), ParseConditions(r.ConditionsJson), r.Confidence, r.Model))
            .ToList();
        var summary = await narratives.GetAsync(assessmentId, Atlas.Domain.Ai.AiNarrative.Kinds.ExecutiveSummary, "summary", Atlas.Domain.Ai.AiNarrative.NormalizeLang(lang), cancellationToken);
        var migrationPlan = await narratives.GetAsync(assessmentId, Atlas.Domain.Ai.AiNarrative.Kinds.MigrationPlan, Atlas.Application.Ai.AiNarrativeService.MigrationPlanKey, Atlas.Domain.Ai.AiNarrative.NormalizeLang(lang), cancellationToken);

        return new ExecutiveReport(header, latestScans, inventories, totals, groups, reportFindings, reportHealth, modernization, comparison, projectRows, verdict,
            recoveredRules.Count == 0 ? null : recoveredRules,
            summary is null ? null : new ReportAiSummary(summary.Text, summary.Model, summary.CreatedAtUtc),
            migrationPlan is null ? null : new ReportAiSummary(migrationPlan.Text, migrationPlan.Model, migrationPlan.CreatedAtUtc));
    }

    private static IReadOnlyList<string> ParseConditions(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private async Task<ReportComparison?> BuildComparisonAsync(Guid assessmentId, string lang, CancellationToken cancellationToken)
    {
        var ordered = (await runs.ListByAssessmentAsync(assessmentId, cancellationToken)).OrderByDescending(r => r.Number).ToList();
        if (ordered.Count < 2)
        {
            return null;
        }

        var comparison = await comparisons.BuildAsync(assessmentId, ordered[0].Id, null, lang, cancellationToken);
        if (comparison?.Previous is null)
        {
            return null;
        }

        return new ReportComparison(
            comparison.Current.Number, comparison.Previous.Number, comparison.HealthDelta,
            comparison.Resolved.Sum(r => r.Count), comparison.New.Sum(r => r.Count), comparison.Regressed.Sum(r => r.Count),
            comparison.Resolved.Take(5).Select(r => $"{r.Title} ×{r.Count}").ToList(),
            comparison.New.Take(5).Select(r => $"{r.Title} ×{r.Count}").ToList());
    }

    private static List<ReportProjectRow> BuildProjectRows(IReadOnlyList<ReportInventory> inventories, IReadOnlyList<ReportFinding> open)
    {
        var projects = inventories.SelectMany(i => i.Projects)
            .Select(p => (p, Folder: (Path.GetDirectoryName(p.Path) ?? string.Empty).Replace('\\', '/').Trim('/')))
            .OrderByDescending(x => x.Folder.Length)
            .ToList();
        if (projects.Count == 0)
        {
            return [];
        }

        var counts = projects.ToDictionary(x => x.p.Path, _ => new int[5]);
        foreach (var finding in open.Where(f => f.FilePath is not null))
        {
            var path = finding.FilePath!.Replace('\\', '/').TrimStart('.', '/');
            var owner = projects.FirstOrDefault(x => x.Folder.Length == 0 || path.StartsWith(x.Folder + "/", StringComparison.OrdinalIgnoreCase));
            if (owner.p is not null)
            {
                counts[owner.p.Path][(int)finding.Severity]++;
            }
        }

        return projects
            .Select(x => new ReportProjectRow(x.p.Name, x.p.TargetFramework, counts[x.p.Path].Sum(),
                counts[x.p.Path][(int)Severity.Critical], counts[x.p.Path][(int)Severity.High], counts[x.p.Path][(int)Severity.Medium], counts[x.p.Path][(int)Severity.Low], x.p.UiFramework))
            .OrderByDescending(r => r.Critical * 15 + r.High * 8 + r.Medium * 3 + r.Low)
            .ThenBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildVerdict(ReportLocale locale, string name, ReportHealth? health, ReportTotals totals, ReportModernization? modernization, IReadOnlyList<ReportProjectRow> projects)
    {
        var c = locale.Culture;
        var critHigh = totals.OpenBySeverity[Severity.Critical] + totals.OpenBySeverity[Severity.High];
        var legacy = projects.Count(p => Atlas.Domain.Modernization.ModernizationProfile.IsLegacyFramework(p.TargetFramework));
        var text = health is null
            ? string.Format(c, locale.VerdictNoHealth, name, totals.Open)
            : string.Format(c, locale.Verdict, name, health.Score, locale.Term(health.RiskLevel), totals.Open, critHigh, projects.Count, legacy);
        if (modernization is not null)
        {
            var e = modernization.RecommendedEstimate;
            text += " " + string.Format(c, locale.VerdictStrategy, modernization.RecommendedName,
                e.OptimisticHours.ToString("N0", c), e.ConservativeHours.ToString("N0", c),
                e.OptimisticMonths.ToString("0.#", c), e.ConservativeMonths.ToString("0.#", c), e.Confidence.ToLowerInvariant());
        }

        return text;
    }

    private async Task<ReportModernization?> BuildModernizationAsync(Guid assessmentId, string lang, CancellationToken cancellationToken)
    {
        var plan = await planBuilder.BuildAsync(assessmentId, cancellationToken);
        if (plan is null)
        {
            return null;
        }

        var recommended = plan.Recommended;
        var strategies = plan.Assessment.Strategies
            .OrderByDescending(s => s.FitScore)
            .Select(s =>
            {
                var estimate = plan.Estimates.First(e => e.Strategy == s.Strategy);
                return new ReportStrategy(
                    ModernizationTexts.Strategy(s.Strategy, lang), s.FitScore, s.Risk.ToString(), s.Strategy == plan.Assessment.Recommended,
                    estimate.EffortHours.Likely, estimate.DurationMonths.Likely, estimate.Cost.Likely, estimate.Cost.Currency,
                    s.Rationale.Select(k => ModernizationTexts.Text(k, lang)).ToList(),
                    s.Blockers.Select(k => ModernizationTexts.Text(k, lang)).ToList());
            })
            .ToList();

        var estimate = new ReportEstimate(
            recommended.EffortHours.Optimistic, recommended.EffortHours.Likely, recommended.EffortHours.Conservative,
            recommended.DurationMonths.Optimistic, recommended.DurationMonths.Likely, recommended.DurationMonths.Conservative,
            recommended.Cost.Optimistic, recommended.Cost.Likely, recommended.Cost.Conservative, recommended.Cost.Currency,
            ModernizationTexts.Confidence(recommended.Confidence, lang),
            recommended.Breakdown.Select(b => (ModernizationTexts.Text(b.Key, lang), Math.Round(b.Hours), b.Quantity)).ToList(),
            recommended.Assumptions.Select(a => (ModernizationTexts.Text(a.Key, lang), a.Key == "assumption.confidence" ? ModernizationTexts.Confidence(recommended.Confidence, lang) : a.Value)).ToList());

        var phases = plan.Roadmap.Phases
            .Select(ph => new ReportPhase(
                ModernizationTexts.Text(ph.Key, lang), ph.EffortShare, ph.EffortHours.Likely, ph.DurationMonths.Likely,
                ph.DependsOn.Select(d => ModernizationTexts.Text(d, lang)).ToList(),
                ph.WorkItems.Select(w => (ModernizationTexts.Text(w.Key, lang), w.Quantity)).ToList()))
            .ToList();

        return new ReportModernization(
            ModernizationTexts.Strategy(plan.Assessment.Recommended, lang),
            ModernizationTexts.StrategyDescription(plan.Assessment.Recommended, lang),
            strategies, estimate, phases,
            $"{plan.Assessment.ModelVersion} · {recommended.ModelVersion} · {plan.Roadmap.ModelVersion}");
    }
}
