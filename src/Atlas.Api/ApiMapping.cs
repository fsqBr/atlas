using Atlas.Application.Assessments;
using Atlas.Connector.Abstractions;
using Atlas.Application.Credentials;
using Atlas.Application.Findings;
using Atlas.Application.Portfolio;
using Atlas.Contracts.Assessments;
using Atlas.Domain.Rules;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Jobs;
using Atlas.Domain.Modernization;
using Atlas.Domain.Scans;

namespace Atlas.Api;

/// <summary>Domain → contract mapping. Evidence text is rendered by the client; keep it data, never markup.</summary>
internal static class ApiMapping
{
    public static AssessmentSummaryResponse ToSummary(Assessment a, HealthSnapshot? health, ScanJobState? activeJob) => new(
        a.Id, a.Name, a.SourceKind, a.SourceLocator, a.Status.ToString(), a.CreatedAtUtc, a.CompletedAtUtc,
        health?.Score, health?.RiskLevel.ToString(), health?.OpenFindings, activeJob?.ToString());

    public static AssessmentResponse ToResponse(Assessment a, IReadOnlyList<Scan> scans, ScanJobState? activeJob) => new(
        a.Id, a.Name, a.SourceKind, a.SourceLocator, a.Branch, a.CredentialName, a.ExcludeGlobs, a.RerunEveryDays, a.WebhookUrl, a.TargetScore, a.TargetDate, a.Status.ToString(), a.FailureReason,
        a.CreatedAtUtc, a.StartedAtUtc, a.CompletedAtUtc,
        scans.Select(ToResponse).ToList(), activeJob?.ToString());

    public static ScanResponse ToResponse(Scan s) => new(
        s.Id, s.ScannerId, s.ScannerVersion, s.CommitSha, s.Status.ToString(), s.Error,
        s.FindingsEmitted, s.FindingsNew, s.FindingsRecurring, s.FindingsResolved, s.FindingsRegressed,
        s.StartedAtUtc, s.FinishedAtUtc);

    /// <summary>Finding text rendered in the reader's language from rule localizations + structured data.</summary>
    public static FindingResponse ToResponse(
        FindingWithLatestOccurrence item, IReadOnlyDictionary<string, RuleDefinition> rules, string? lang, FindingSuppression? suppression = null)
    {
        var f = item.Finding;
        var o = item.Latest;
        var text = FindingLocalizer.Localize(f, o, rules.GetValueOrDefault(f.RuleId), lang);
        return new FindingResponse(
            f.Id, f.RuleId, f.Category.ToString(), f.Severity.ToString(), f.Status.ToString(), f.Origin.ToString(),
            text.Title, text.Message, o?.Confidence.ToString(), text.Remediation,
            o?.Evidence.FilePath, o?.Evidence.LineStart, o?.Evidence.LineEnd, o?.Evidence.Symbol, o?.Evidence.ScannerId,
            f.CreatedAtUtc, f.UpdatedAtUtc,
            suppression is null ? null : new SuppressionResponse(suppression.Kind.ToString(), suppression.Reason, suppression.Author, suppression.CreatedAtUtc));
    }

    public static RunResponse ToResponse(AssessmentRun r) => new(
        r.Id, r.Number, r.CommitSha, r.Status.ToString(), r.FailureReason, r.StartedAtUtc, r.FinishedAtUtc,
        r.HealthScore, r.OpenFindings, r.FindingsNew, r.FindingsRecurring, r.FindingsResolved, r.FindingsRegressed,
        r.ScannersRun, r.ScannersFailed);

    public static RunResponse ToResponse(RunSummary r) => new(
        r.RunId, r.Number, r.CommitSha, r.Status, null, r.StartedAtUtc, r.FinishedAtUtc,
        r.HealthScore, r.OpenFindings, r.FindingsNew, r.FindingsRecurring, r.FindingsResolved, r.FindingsRegressed,
        r.ScannersRun, r.ScannersFailed);

    public static RunComparisonResponse ToResponse(RunComparison c) => new(
        ToResponse(c.Current),
        c.Previous is null ? null : ToResponse(c.Previous),
        c.SameCommit,
        c.HealthDelta,
        c.Dimensions.Select(d => new DimensionDeltaResponse(d.Name, d.Before, d.After, d.Delta)).ToList(),
        c.Resolved.Select(ToResponse).ToList(),
        c.New.Select(ToResponse).ToList(),
        c.Regressed.Select(ToResponse).ToList(),
        c.Inventory is null
            ? null
            : new InventoryDeltaResponse(c.Inventory.LinesBefore, c.Inventory.LinesAfter, c.Inventory.FilesBefore,
                c.Inventory.FilesAfter, c.Inventory.ProjectsBefore, c.Inventory.ProjectsAfter));

    private static RuleDeltaResponse ToResponse(RuleDelta r) => new(
        r.RuleId, r.Title, r.Category.ToString(), r.MaxSeverity.ToString(), r.Count, r.SampleLocations);

    public static HealthResponse ToResponse(HealthSnapshot snapshot) => new(
        snapshot.Score, snapshot.RiskLevel.ToString(), snapshot.ModelVersion, snapshot.Explanation,
        snapshot.OpenFindings, snapshot.ProjectCount, snapshot.CommitSha, snapshot.CreatedAtUtc,
        HealthSnapshotFactory.ReadDimensions(snapshot)
            .Select(d => new HealthDimensionResponse(d.Name, d.Weight, d.Score, d.Penalty,
                d.Contributors.Select(c => new HealthContributorResponse(c.RuleId, c.Count, c.Points)).ToList()))
            .ToList());

    public static CredentialResponse ToResponse(CredentialSummary c) => new(
        c.Name, c.Username, c.Description, c.CreatedAtUtc, c.UpdatedAtUtc, c.LastUsedAtUtc, c.UsedByAssessments);

    public static DiscoveredRepositoryResponse ToResponse(RepositoryInfo r) => new(
        r.Name, r.Locator, r.Kind, r.DefaultBranch, r.Archived, r.Language, r.LastPushUtc, r.IsPrivate);

    public static ModernizationPlanResponse ToResponse(ModernizationPlan plan, string? lang)
    {
        var p = plan.Profile;
        var profile = new ModernizationProfileResponse(
            p.LinesOfCode, p.Projects, p.LegacyFrameworkProjects, p.ModernFrameworkProjects, p.UnknownFrameworkProjects,
            p.LegacyProjectFormat, p.PrerequisiteBlockers, p.HighBlockers, p.MediumBlockers, p.ProjectsWithBlockers,
            p.CriticalSecurity, p.HighSecurity, p.MediumSecurity, p.SecretsFound, p.VulnerablePackages,
            p.HasTests, p.CoverageLineRate, p.ProjectsWithoutTests, p.ArchitectureCycles, p.Tier);

        var strategies = plan.Assessment.Strategies
            .OrderByDescending(s => s.FitScore)
            .Select(s => new StrategyResponse(
                s.Strategy.ToString(),
                ModernizationTexts.Strategy(s.Strategy, lang),
                ModernizationTexts.StrategyDescription(s.Strategy, lang),
                s.FitScore,
                s.Risk.ToString(),
                s.Strategy == plan.Assessment.Recommended,
                s.Rationale.Select(k => ModernizationTexts.Text(k, lang)).ToList(),
                s.Prerequisites.Select(k => ModernizationTexts.Text(k, lang)).ToList(),
                s.Blockers.Select(k => ModernizationTexts.Text(k, lang)).ToList(),
                s.Benefits.Select(k => ModernizationTexts.Text(k, lang)).ToList(),
                ToResponse(plan.Estimates.First(e => e.Strategy == s.Strategy), lang)))
            .ToList();

        var roadmap = new RoadmapResponse(
            plan.Roadmap.ModelVersion,
            plan.Roadmap.Strategy.ToString(),
            plan.Roadmap.Phases.Select(ph => new PhaseResponse(
                ph.Key, ModernizationTexts.Text(ph.Key, lang), ph.Order, ph.EffortShare,
                ToResponse(ph.EffortHours), ToResponse(ph.DurationMonths),
                ph.DependsOn, ph.DependsOn.Select(d => ModernizationTexts.Text(d, lang)).ToList(),
                ph.WorkItems.Select(w => new WorkItemResponse(w.Key, ModernizationTexts.Text(w.Key, lang), w.Quantity)).ToList())).ToList());

        return new ModernizationPlanResponse(
            plan.Assessment.ModelVersion, profile, plan.Assessment.Recommended.ToString(),
            ModernizationTexts.Strategy(plan.Assessment.Recommended, lang), strategies, roadmap);
    }

    public static EstimateResponse ToResponse(CostEstimate e, string? lang) => new(
        e.ModelVersion, ToResponse(e.EffortHours), ToResponse(e.DurationMonths),
        new MoneyRangeResponse(e.Cost.Optimistic, e.Cost.Likely, e.Cost.Conservative, e.Cost.Currency),
        e.Confidence.ToString(), ModernizationTexts.Confidence(e.Confidence, lang),
        e.Breakdown.Select(b => new EffortItemResponse(b.Key, ModernizationTexts.Text(b.Key, lang), Math.Round(b.Hours), b.Quantity)).ToList(),
        e.Assumptions.Select(a => new AssumptionResponse(a.Key, ModernizationTexts.Text(a.Key, lang), a.Key == "assumption.confidence" ? ModernizationTexts.Confidence(e.Confidence, lang) : a.Value)).ToList());

    private static RangeResponse ToResponse(Atlas.Domain.Modernization.Range r) => new(r.Optimistic, r.Likely, r.Conservative);

    public static PortfolioResponse ToResponse(PortfolioSummary p) => new(
        p.Assessments, p.Assessed, p.AverageScore,
        p.ByRisk.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        p.Lines, p.Files, p.Projects, p.LegacyProjects, p.ModernProjects, p.UnknownProjects,
        p.Frameworks.Select(f => new PortfolioFrameworkResponse(f.Framework, f.Count, Atlas.Domain.Modernization.ModernizationProfile.IsLegacyFramework(f.Framework == "unknown" ? null : f.Framework))).ToList(),
        p.OpenFindings,
        p.OpenBySeverity.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        p.OpenByCategory.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        p.TopRules.Select(r => new PortfolioRuleResponse(r.RuleId, r.Title, r.Category.ToString(), r.MaxSeverity.ToString(), r.Count, r.Assessments)).ToList(),
        p.Rows.Select(r => new PortfolioRowResponse(r.Id, r.Name, r.SourceKind, r.Status, r.Score, r.Risk?.ToString(), r.OpenFindings, r.Lines, r.Projects, r.LegacyProjects, r.CompletedAtUtc, r.Percentile, r.TargetScore, r.TargetDate, r.TargetStatus.ToString())).ToList(),
        (p.Benchmark?.Dimensions ?? []).Select(d => new BenchmarkDimensionResponse(d.Name, d.Count, d.P25, d.P50, d.P75, d.Best, d.Worst)).ToList(),
        (p.Targets ?? new Dictionary<Atlas.Domain.Assessments.TargetStatus, int>()).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));

    public static SuppressionPolicyResponse ToResponse(SuppressionPolicy p) => new(p.Id, p.AssessmentId, p.RulePattern, p.PathGlob, p.Reason, p.Author, p.CreatedAtUtc);

    public static ActualResponse ToResponse(ModernizationActual a, string? lang) => new(
        a.AssessmentId, a.Strategy.ToString(), ModernizationTexts.Strategy(a.Strategy, lang), a.ActualHours, a.ActualMonths, a.ActualCost, a.Currency, a.Notes, a.RecordedBy, a.RecordedAtUtc);

    public static CalibrationResponse ToResponse(CalibrationSummary c, string? lang) => new(
        c.Points, c.MeanRatio, c.MedianRatio, c.Recommendation, ModernizationTexts.Text(c.Recommendation, lang),
        c.Items.Select(i => new CalibrationPointResponse(i.AssessmentId, i.AssessmentName, i.Strategy.ToString(), ModernizationTexts.Strategy(i.Strategy, lang), i.EstimatedLikelyHours, i.ActualHours, i.Ratio, i.Notes, i.RecordedAtUtc)).ToList());

    public static BusinessRuleAnalysisResponse ToResponse(Atlas.Domain.Ai.BusinessRuleAnalysis a) =>
        new(a.Id, a.Provider.ToString(), a.Model, a.Status.ToString(), a.CandidatesFound, a.SnippetsSent, a.RulesFound, a.InputTokens, a.OutputTokens, a.Error, a.StartedAtUtc, a.CompletedAtUtc);

    public static BusinessRuleResponse ToResponse(Atlas.Domain.Ai.BusinessRule r, bool portuguese)
    {
        IReadOnlyList<string> conditions;
        try
        {
            conditions = System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.ConditionsJson) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            conditions = [];
        }

        return new BusinessRuleResponse(r.Id, r.FilePath, r.Symbol, r.StartLine, r.Name, portuguese ? r.DescriptionPt : r.DescriptionEn, r.Category.ToString(), conditions, r.Confidence, r.Model, r.CreatedAtUtc, r.Rating, r.FeedbackComment);
    }

    public static AccessResponse ToResponse(Atlas.Application.Assessments.AccessView v) =>
        new(v.Restricted, v.MyRole?.ToString(), v.CanManage, v.CanEdit,
            v.Entries.Select(e => new AccessEntryResponse(e.Id, e.Subject, e.SubjectName, e.Role.ToString(), e.GrantedBy, e.GrantedAtUtc)).ToList());

    public static SideBySideResponse ToResponse(Atlas.Application.Portfolio.SideBySideComparison c) =>
        new(ToResponse(c.A), ToResponse(c.B), c.RuleDifferences.Select(d => new RuleDifferenceResponse(d.RuleId, d.Title, d.Category.ToString(), d.MaxSeverity.ToString(), d.CountA, d.CountB)).ToList());

    private static ComparisonSideResponse ToResponse(Atlas.Application.Portfolio.ComparisonSide s) =>
        new(s.Id, s.Name, s.SourceKind, s.Status, s.CompletedAtUtc, s.Score, s.Risk?.ToString(), s.Dimensions, s.OpenFindings,
            s.OpenBySeverity.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), s.OpenByCategory.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            s.Lines, s.Files, s.Projects, s.LegacyProjects, s.UiFrameworks, s.RecommendedStrategy, s.LikelyHours, s.LikelyCost, s.Currency, s.TargetScore, s.TopRules);
}
