using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Rules;

namespace Atlas.Application.Assessments;

public sealed record RunSummary(
    Guid RunId,
    int Number,
    string? CommitSha,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int? HealthScore,
    int? OpenFindings,
    int FindingsNew,
    int FindingsRecurring,
    int FindingsResolved,
    int FindingsRegressed,
    int ScannersRun,
    int ScannersFailed);

public sealed record DimensionDelta(string Name, int? Before, int After, int? Delta);

public sealed record RuleDelta(
    string RuleId,
    string Title,
    FindingCategory Category,
    Severity MaxSeverity,
    int Count,
    IReadOnlyList<string> SampleLocations);

public sealed record InventoryDelta(
    long LinesBefore, long LinesAfter,
    int FilesBefore, int FilesAfter,
    int ProjectsBefore, int ProjectsAfter);

/// <summary>What changed between two runs — every number traces to persisted findings and snapshots.</summary>
public sealed record RunComparison(
    RunSummary Current,
    RunSummary? Previous,
    bool SameCommit,
    int? HealthDelta,
    IReadOnlyList<DimensionDelta> Dimensions,
    IReadOnlyList<RuleDelta> Resolved,
    IReadOnlyList<RuleDelta> New,
    IReadOnlyList<RuleDelta> Regressed,
    InventoryDelta? Inventory);

/// <summary>Pure diff logic (no I/O) so the "what improved" story is unit-testable.</summary>
public static class RunDiff
{
    private const int SampleLocations = 5;

    public static RunSummary Summarize(AssessmentRun run) => new(
        run.Id, run.Number, run.CommitSha, run.Status.ToString(), run.StartedAtUtc, run.FinishedAtUtc,
        run.HealthScore, run.OpenFindings, run.FindingsNew, run.FindingsRecurring, run.FindingsResolved,
        run.FindingsRegressed, run.ScannersRun, run.ScannersFailed);

    public static RunComparison Compute(
        AssessmentRun current,
        AssessmentRun? previous,
        IReadOnlyCollection<Guid> currentScanIds,
        IReadOnlyList<FindingWithLatestOccurrence> touched,
        IReadOnlyList<HealthDimension> currentDimensions,
        IReadOnlyList<HealthDimension>? previousDimensions,
        IReadOnlyList<InventorySnapshot> currentInventory,
        IReadOnlyList<InventorySnapshot> previousInventory,
        IReadOnlyDictionary<string, RuleDefinition> rules,
        string? lang = null)
    {
        var scanIds = currentScanIds.ToHashSet();

        var created = touched.Where(f => scanIds.Contains(f.Finding.FirstSeenScanId)).ToList();
        var resolved = touched.Where(f => f.Finding.ResolvedScanId is { } r && scanIds.Contains(r)).ToList();
        var regressed = touched.Where(f => f.Finding.Status == FindingStatus.Regressed && scanIds.Contains(f.Finding.LastSeenScanId)
                                           && !scanIds.Contains(f.Finding.FirstSeenScanId)).ToList();

        var previousByName = previousDimensions?.ToDictionary(d => d.Name, d => d.Score, StringComparer.Ordinal);
        var dimensions = currentDimensions
            .Select(d =>
            {
                int? before = previousByName is not null && previousByName.TryGetValue(d.Name, out var b) ? b : null;
                return new DimensionDelta(d.Name, before, d.Score, before is null ? null : d.Score - before);
            })
            .ToList();

        InventoryDelta? inventory = null;
        if (currentInventory.Count > 0 && previousInventory.Count > 0)
        {
            inventory = new InventoryDelta(
                previousInventory.Sum(i => i.TotalLines), currentInventory.Sum(i => i.TotalLines),
                previousInventory.Sum(i => i.FileCount), currentInventory.Sum(i => i.FileCount),
                previousInventory.Sum(i => i.ProjectCount), currentInventory.Sum(i => i.ProjectCount));
        }

        return new RunComparison(
            Summarize(current),
            previous is null ? null : Summarize(previous),
            SameCommit: previous?.CommitSha is not null && previous.CommitSha == current.CommitSha,
            HealthDelta: previous?.HealthScore is { } ph && current.HealthScore is { } ch ? ch - ph : null,
            dimensions,
            GroupByRule(resolved, rules, lang),
            GroupByRule(created, rules, lang),
            GroupByRule(regressed, rules, lang),
            inventory);
    }

    private static IReadOnlyList<RuleDelta> GroupByRule(
        IReadOnlyList<FindingWithLatestOccurrence> findings, IReadOnlyDictionary<string, RuleDefinition> rules, string? lang) =>
        findings
            .GroupBy(f => f.Finding.RuleId, StringComparer.Ordinal)
            .Select(g => new RuleDelta(
                g.Key,
                Findings.FindingLocalizer.RuleTitle(rules.GetValueOrDefault(g.Key), g.Key, lang),
                g.First().Finding.Category,
                g.Max(f => f.Finding.Severity),
                g.Count(),
                g.Select(Location).Where(l => l.Length > 0).Distinct().Take(SampleLocations).ToList()))
            .OrderByDescending(r => r.MaxSeverity)
            .ThenByDescending(r => r.Count)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToList();

    private static string Location(FindingWithLatestOccurrence f)
    {
        var e = f.Latest?.Evidence;
        if (e is null)
        {
            return string.Empty;
        }

        return e.FilePath is null ? e.Symbol ?? string.Empty : e.LineStart is null ? e.FilePath : $"{e.FilePath}:{e.LineStart}";
    }
}

/// <summary>Loads the persisted facts for two runs and hands them to RunDiff.</summary>
public sealed class RunComparisonBuilder(
    IAssessmentRunRepository runs,
    IScanRepository scans,
    IFindingRepository findings,
    IHealthRepository health,
    IInventoryRepository inventory,
    IRuleCatalog rules)
{
    /// <param name="withRunId">Run to compare against; defaults to the previous run by number.</param>
    public Task<RunComparison?> BuildAsync(Guid assessmentId, Guid runId, Guid? withRunId, CancellationToken cancellationToken) =>
        BuildAsync(assessmentId, runId, withRunId, null, cancellationToken);

    public async Task<RunComparison?> BuildAsync(Guid assessmentId, Guid runId, Guid? withRunId, string? lang, CancellationToken cancellationToken)
    {
        var current = await runs.GetAsync(runId, cancellationToken);
        if (current is null || current.AssessmentId != assessmentId)
        {
            return null;
        }

        var all = await runs.ListByAssessmentAsync(assessmentId, cancellationToken);
        var previous = withRunId is { } w
            ? all.FirstOrDefault(r => r.Id == w)
            : all.Where(r => r.Number < current.Number).OrderByDescending(r => r.Number).FirstOrDefault();

        var currentScans = await scans.ListByRunAsync(runId, cancellationToken);
        var scanIds = currentScans.Select(s => s.Id).ToList();
        var touched = scanIds.Count == 0
            ? []
            : await findings.ListTouchedByScansAsync(assessmentId, scanIds, cancellationToken);

        var currentHealth = await health.GetByRunAsync(runId, cancellationToken);
        var previousHealth = previous is null ? null : await health.GetByRunAsync(previous.Id, cancellationToken);
        var currentInventory = await inventory.GetByRunAsync(runId, cancellationToken);
        var previousInventory = previous is null ? [] : await inventory.GetByRunAsync(previous.Id, cancellationToken);
        var catalog = await rules.GetAllAsync(cancellationToken);

        return RunDiff.Compute(
            current, previous, scanIds, touched,
            currentHealth is null ? [] : HealthSnapshotFactory.ReadDimensions(currentHealth),
            previousHealth is null ? null : HealthSnapshotFactory.ReadDimensions(previousHealth),
            currentInventory, previousInventory, catalog, lang);
    }
}
