using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Modernization;

namespace Atlas.Application.Portfolio;

public sealed record ComparisonSide(
    Guid Id,
    string Name,
    string SourceKind,
    string Status,
    DateTimeOffset? CompletedAtUtc,
    int? Score,
    RiskLevel? Risk,
    IReadOnlyDictionary<string, int> Dimensions,
    int OpenFindings,
    IReadOnlyDictionary<Severity, int> OpenBySeverity,
    IReadOnlyDictionary<FindingCategory, int> OpenByCategory,
    long Lines,
    int Files,
    int Projects,
    int LegacyProjects,
    IReadOnlyDictionary<string, int> UiFrameworks,
    string? RecommendedStrategy,
    double? LikelyHours,
    decimal? LikelyCost,
    string? Currency,
    int? TargetScore,
    IReadOnlyDictionary<string, int> TopRules);

/// <summary>A rule present in one side and not (or far less) in the other — the interesting differences.</summary>
public sealed record RuleDifference(string RuleId, string Title, FindingCategory Category, Severity MaxSeverity, int CountA, int CountB);

public sealed record SideBySideComparison(ComparisonSide A, ComparisonSide B, IReadOnlyList<RuleDifference> RuleDifferences);

/// <summary>
/// Two assessments, same columns: health per dimension, findings, size, UI stack,
/// recommended strategy with likely effort/cost, and the rules that set them apart.
/// Reads persisted snapshots only (same sources as the portfolio) — no re-analysis.
/// </summary>
public sealed class SideBySideComparisonBuilder(
    IAssessmentRepository assessments,
    IHealthRepository health,
    IInventoryRepository inventory,
    IFindingRepository findings,
    IRuleCatalog rules,
    ModernizationPlanBuilder plans)
{
    private const int TopRulesPerSide = 8;
    private const int MaxDifferences = 12;

    public async Task<SideBySideComparison?> BuildAsync(Guid a, Guid b, string? lang, CancellationToken cancellationToken)
    {
        var left = await assessments.GetAsync(a, cancellationToken);
        var right = await assessments.GetAsync(b, cancellationToken);
        if (left is null || right is null)
        {
            return null;
        }

        var ids = new[] { a, b };
        var scores = await health.GetLatestForAsync(ids, cancellationToken);
        var inventories = await inventory.GetLatestForAsync(ids, cancellationToken);
        var open = await findings.SummarizeOpenAsync(ids, cancellationToken);
        var catalog = await rules.GetAllAsync(cancellationToken);
        var planA = await plans.BuildAsync(a, cancellationToken);
        var planB = await plans.BuildAsync(b, cancellationToken);

        var sideA = Side(left, scores, inventories, open, catalog, planA, lang);
        var sideB = Side(right, scores, inventories, open, catalog, planB, lang);

        var byRule = open.GroupBy(o => o.RuleId, StringComparer.Ordinal)
            .Select(g => new RuleDifference(
                g.Key,
                FindingLocalizer.RuleTitle(catalog.GetValueOrDefault(g.Key), g.Key, lang),
                g.First().Category,
                g.Max(o => o.Severity),
                g.Where(o => o.AssessmentId == a).Sum(o => o.Count),
                g.Where(o => o.AssessmentId == b).Sum(o => o.Count)))
            .Where(d => d.CountA != d.CountB)
            .OrderByDescending(d => d.MaxSeverity)
            .ThenByDescending(d => Math.Abs(d.CountA - d.CountB))
            .Take(MaxDifferences)
            .ToList();

        return new SideBySideComparison(sideA, sideB, byRule);
    }

    private static ComparisonSide Side(
        Domain.Assessments.Assessment assessment,
        IReadOnlyDictionary<Guid, Domain.Health.HealthSnapshot> scores,
        IReadOnlyDictionary<Guid, IReadOnlyList<Domain.Assessments.InventorySnapshot>> inventories,
        IReadOnlyList<OpenFindingSummary> open,
        IReadOnlyDictionary<string, Domain.Rules.RuleDefinition> catalog,
        ModernizationPlan? plan,
        string? lang)
    {
        var snapshot = scores.GetValueOrDefault(assessment.Id);
        var snapshots = inventories.TryGetValue(assessment.Id, out var s) ? s : [];
        var projects = snapshots.SelectMany(InventorySnapshotFactory.ReadProjects).ToList();
        var mine = open.Where(o => o.AssessmentId == assessment.Id).ToList();
        var recommended = plan?.Recommended;

        return new ComparisonSide(
            assessment.Id, assessment.Name, assessment.SourceKind, assessment.Status.ToString(), assessment.CompletedAtUtc,
            snapshot?.Score, snapshot?.RiskLevel,
            snapshot is null ? new Dictionary<string, int>() : HealthSnapshotFactory.ReadDimensions(snapshot).ToDictionary(d => d.Name, d => d.Score, StringComparer.Ordinal),
            mine.Sum(o => o.Count),
            Enum.GetValues<Severity>().ToDictionary(sv => sv, sv => mine.Where(o => o.Severity == sv).Sum(o => o.Count)),
            Enum.GetValues<FindingCategory>().ToDictionary(c => c, c => mine.Where(o => o.Category == c).Sum(o => o.Count)),
            snapshots.Sum(x => x.TotalLines), snapshots.Sum(x => x.FileCount), projects.Count,
            projects.Count(p => ModernizationProfile.IsLegacyFramework(p.TargetFramework)),
            projects.Where(p => p.UiFramework is not null).GroupBy(p => p.UiFramework!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            plan is null ? null : ModernizationTexts.Strategy(plan.Assessment.Recommended, lang),
            recommended?.EffortHours.Likely, recommended?.Cost.Likely, recommended?.Cost.Currency,
            assessment.TargetScore,
            mine.GroupBy(o => o.RuleId, StringComparer.Ordinal)
                .Select(g => (Title: FindingLocalizer.RuleTitle(catalog.GetValueOrDefault(g.Key), g.Key, lang), Count: g.Sum(o => o.Count), Sev: g.Max(o => o.Severity)))
                .OrderByDescending(x => x.Sev).ThenByDescending(x => x.Count).Take(TopRulesPerSide)
                .ToDictionary(x => x.Title, x => x.Count, StringComparer.Ordinal));
    }
}
