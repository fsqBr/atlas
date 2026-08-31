using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Modernization;

namespace Atlas.Application.Portfolio;

/// <summary>Open findings of one assessment grouped by rule and severity (one aggregate query).</summary>
public sealed record OpenFindingSummary(Guid AssessmentId, string RuleId, FindingCategory Category, Severity Severity, int Count);

public sealed record PortfolioRule(string RuleId, string Title, FindingCategory Category, Severity MaxSeverity, int Count, int Assessments);

public sealed record PortfolioRow(
    Guid Id,
    string Name,
    string SourceKind,
    string Status,
    int? Score,
    RiskLevel? Risk,
    int? OpenFindings,
    long Lines,
    int Projects,
    int LegacyProjects,
    DateTimeOffset? CompletedAtUtc,
    int? Percentile = null,
    int? TargetScore = null,
    DateTimeOffset? TargetDate = null,
    TargetStatus TargetStatus = TargetStatus.None,
    IReadOnlyList<string>? Tags = null);

public sealed record PortfolioSummary(
    int Assessments,
    int Assessed,
    double? AverageScore,
    IReadOnlyDictionary<RiskLevel, int> ByRisk,
    long Lines,
    int Files,
    int Projects,
    int LegacyProjects,
    int ModernProjects,
    int UnknownProjects,
    IReadOnlyList<(string Framework, int Count)> Frameworks,
    int OpenFindings,
    IReadOnlyDictionary<Severity, int> OpenBySeverity,
    IReadOnlyDictionary<FindingCategory, int> OpenByCategory,
    IReadOnlyList<PortfolioRule> TopRules,
    IReadOnlyList<PortfolioRow> Rows,
    PortfolioBenchmark? Benchmark = null,
    IReadOnlyDictionary<TargetStatus, int>? Targets = null);

/// <summary>
/// The estate view ("Executive Dashboard"): every assessment's
/// latest health, inventory and open findings folded into one picture. Reads
/// persisted snapshots only — a few aggregate queries, no re-analysis.
/// </summary>
public sealed class PortfolioBuilder(
    IAssessmentRepository assessments,
    IHealthRepository health,
    IInventoryRepository inventory,
    IFindingRepository findings,
    IRuleCatalog rules)
{
    private const int MaxAssessments = 500;
    private const int TopRules = 15;

    public async Task<PortfolioSummary> BuildAsync(string? lang, CancellationToken cancellationToken)
    {
        var list = await assessments.ListRecentAsync(MaxAssessments, cancellationToken);
        var ids = list.Select(a => a.Id).ToList();
        var scores = await health.GetLatestForAsync(ids, cancellationToken);
        var inventories = await inventory.GetLatestForAsync(ids, cancellationToken);
        var open = await findings.SummarizeOpenAsync(ids, cancellationToken);
        var catalog = await rules.GetAllAsync(cancellationToken);

        var rows = new List<PortfolioRow>();
        long lines = 0;
        var files = 0;
        var legacy = 0;
        var modern = 0;
        var unknown = 0;
        var frameworks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var assessment in list)
        {
            var snapshots = inventories.TryGetValue(assessment.Id, out var s) ? s : [];
            var projects = snapshots.SelectMany(InventorySnapshotFactory.ReadProjects).ToList();
            var rowLegacy = projects.Count(p => ModernizationProfile.IsLegacyFramework(p.TargetFramework));
            var rowUnknown = projects.Count(p => string.IsNullOrWhiteSpace(p.TargetFramework));

            lines += snapshots.Sum(x => x.TotalLines);
            files += snapshots.Sum(x => x.FileCount);
            legacy += rowLegacy;
            unknown += rowUnknown;
            modern += projects.Count - rowLegacy - rowUnknown;
            foreach (var project in projects)
            {
                var key = string.IsNullOrWhiteSpace(project.TargetFramework) ? "unknown" : project.TargetFramework.Trim().ToLowerInvariant();
                frameworks[key] = frameworks.GetValueOrDefault(key) + 1;
            }

            var score = scores.GetValueOrDefault(assessment.Id);
            rows.Add(new PortfolioRow(
                assessment.Id, assessment.Name, assessment.SourceKind, assessment.Status.ToString(),
                score?.Score, score?.RiskLevel, score?.OpenFindings,
                snapshots.Sum(x => x.TotalLines), projects.Count, rowLegacy, assessment.CompletedAtUtc,
                null, assessment.TargetScore, assessment.TargetDate, assessment.TargetStatusAt(score?.Score, DateTimeOffset.UtcNow), assessment.Tags));
        }

        // Benchmark: where each assessment sits in the estate, per dimension and overall.
        var allScores = rows.Where(r => r.Score is not null).Select(r => r.Score!.Value).ToList();
        rows = rows.Select(r => r.Score is { } sc ? r with { Percentile = Benchmark.PercentileRank(allScores, sc) } : r).ToList();
        var dimensionScores = scores.Values
            .SelectMany(HealthSnapshotFactory.ReadDimensions)
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .Select(g => Benchmark.Describe(g.Key, g.Select(d => d.Score)))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        var benchmark = allScores.Count == 0 ? null : new PortfolioBenchmark([Benchmark.Describe("Overall", allScores), .. dimensionScores]);
        var targets = Enum.GetValues<TargetStatus>().ToDictionary(t => t, t => rows.Count(r => r.TargetStatus == t));

        var assessed = rows.Where(r => r.Score is not null).ToList();
        var byRisk = Enum.GetValues<RiskLevel>().ToDictionary(r => r, r => assessed.Count(a => a.Risk == r));

        var topRules = open
            .Where(o => o.RuleId != "dependency.migration-blocker") // retired umbrella id kept only by never-rerun assessments
            .GroupBy(o => o.RuleId, StringComparer.Ordinal)
            .Select(g => new PortfolioRule(
                g.Key,
                FindingLocalizer.RuleTitle(catalog.GetValueOrDefault(g.Key), g.Key, lang),
                g.First().Category,
                g.Max(o => o.Severity),
                g.Sum(o => o.Count),
                g.Select(o => o.AssessmentId).Distinct().Count()))
            .OrderByDescending(r => r.MaxSeverity)
            .ThenByDescending(r => r.Count)
            .Take(TopRules)
            .ToList();

        return new PortfolioSummary(
            Assessments: rows.Count,
            Assessed: assessed.Count,
            AverageScore: assessed.Count == 0 ? null : Math.Round(assessed.Average(a => a.Score!.Value), 1),
            ByRisk: byRisk,
            Lines: lines,
            Files: files,
            Projects: legacy + modern + unknown,
            LegacyProjects: legacy,
            ModernProjects: modern,
            UnknownProjects: unknown,
            Frameworks: frameworks.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList(),
            OpenFindings: open.Sum(o => o.Count),
            OpenBySeverity: Enum.GetValues<Severity>().ToDictionary(s => s, s => open.Where(o => o.Severity == s).Sum(o => o.Count)),
            OpenByCategory: Enum.GetValues<FindingCategory>().ToDictionary(c => c, c => open.Where(o => o.Category == c).Sum(o => o.Count)),
            TopRules: topRules,
            Rows: rows.OrderBy(r => r.Score ?? int.MaxValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Benchmark: benchmark,
            Targets: targets);
    }
}
