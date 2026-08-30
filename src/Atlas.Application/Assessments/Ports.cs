using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Jobs;
using Atlas.Domain.Rules;
using Atlas.Domain.Scans;
using Atlas.Scanner.Abstractions;

namespace Atlas.Application.Assessments;

public interface IAssessmentRepository
{
    Task<Assessment?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Assessment>> ListRecentAsync(int take, CancellationToken cancellationToken);

    /// <summary>Every assessment id visible to the current tenant.</summary>
    Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken cancellationToken);

    void Add(Assessment assessment);

    /// <summary>Deletes the aggregate; dependents go with it through cascading foreign keys.</summary>
    void Remove(Assessment assessment);

    /// <summary>CI lookup: the assessment for a repository (locator compared case-insensitively, trailing '/' and '.git' ignored), optionally by branch.</summary>
    Task<Assessment?> FindByLocatorAsync(string sourceKind, string locator, string? branch, CancellationToken cancellationToken);

    /// <summary>Locators of every assessment of one source kind (upload GC uses it to know which archives are live).</summary>
    Task<IReadOnlyList<string>> ListSourceLocatorsAsync(string sourceKind, CancellationToken cancellationToken);
}

public interface IAssessmentRunRepository
{
    void Add(AssessmentRun run);

    /// <summary>Every health snapshot in the tenant (runs and triage recomputes), reduced to the trend's needs.</summary>
    Task<IReadOnlyList<Atlas.Application.Portfolio.CompletedRunPoint>> ListCompletedPointsAsync(CancellationToken cancellationToken);

    Task<AssessmentRun?> GetAsync(Guid runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AssessmentRun>> ListByAssessmentAsync(Guid assessmentId, CancellationToken cancellationToken);

    Task<int> NextNumberAsync(Guid assessmentId, CancellationToken cancellationToken);
}

public interface IScanRepository
{
    void Add(Scan scan);

    Task<IReadOnlyList<Scan>> ListByAssessmentAsync(Guid assessmentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Scan>> ListByRunAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Idempotency check: the same scanner on the same commit never runs twice.</summary>
    Task<bool> HasSucceededAsync(Guid assessmentId, string scannerId, string commitSha, CancellationToken cancellationToken);
}

public sealed record FindingWithLatestOccurrence(Finding Finding, FindingOccurrence? Latest);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total);

/// <summary>Optional filters for finding lists; null means "any".</summary>
public sealed record FindingFilter(
    Severity? Severity = null,
    FindingCategory? Category = null,
    FindingStatus? Status = null,
    string? RuleId = null,
    string? Search = null)
{
    public static readonly FindingFilter None = new();
}

public interface ISuppressionRepository
{
    void Add(FindingSuppression suppression);

    Task<FindingSuppression?> GetActiveAsync(Guid findingId, CancellationToken cancellationToken);

    /// <summary>Active suppressions for a set of findings (one query for list views).</summary>
    Task<IReadOnlyDictionary<Guid, FindingSuppression>> GetActiveForAsync(
        IReadOnlyCollection<Guid> findingIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<FindingSuppression>> ListByAssessmentAsync(Guid assessmentId, CancellationToken cancellationToken);
}

public interface IFindingRepository
{
    Task<Finding?> GetAsync(Guid findingId, CancellationToken cancellationToken);

    /// <summary>The most recent occurrence of one finding (message, evidence, remediation as last seen).</summary>
    Task<FindingOccurrence?> GetLatestOccurrenceAsync(Guid findingId, CancellationToken cancellationToken);

    /// <summary>Existing findings for the rules a scanner owns — the coverage-aware set the reconciler may resolve.</summary>
    Task<IReadOnlyList<Finding>> GetByAssessmentAndRulesAsync(
        Guid assessmentId,
        IReadOnlyCollection<string> ruleIds,
        CancellationToken cancellationToken);

    void AddRange(IEnumerable<Finding> findings);

    void AddOccurrences(IEnumerable<FindingOccurrence> occurrences);

    /// <summary>Open + regressed findings of the assessment — the health score input.</summary>
    Task<IReadOnlyList<Finding>> ListOpenAsync(Guid assessmentId, CancellationToken cancellationToken);

    /// <summary>Open + regressed findings across assessments, grouped by rule and severity (portfolio view).</summary>
    Task<IReadOnlyList<Atlas.Application.Portfolio.OpenFindingSummary>> SummarizeOpenAsync(IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken);

    /// <summary>Findings first seen, resolved or last seen by any of the given scans (a run's worth), with latest occurrence.</summary>
    Task<IReadOnlyList<FindingWithLatestOccurrence>> ListTouchedByScansAsync(
        Guid assessmentId, IReadOnlyCollection<Guid> scanIds, CancellationToken cancellationToken);

    Task<Page<FindingWithLatestOccurrence>> ListAsync(
        Guid assessmentId,
        int skip,
        int take,
        CancellationToken cancellationToken,
        FindingFilter? filter = null);
}

public interface IRuleCatalog
{
    /// <summary>Creates or updates the scanner's rules; returns the catalog entries keyed by rule id.</summary>
    Task<IReadOnlyDictionary<string, RuleDefinition>> UpsertAsync(
        string scannerId,
        IReadOnlyList<RuleSpec> rules,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, RuleDefinition>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Every rule id the scanner has ever declared — retired rules included, so their findings get resolved.</summary>
    Task<IReadOnlyList<string>> ListRuleIdsByScannerAsync(string scannerId, CancellationToken cancellationToken);
}

public interface IHealthRepository
{
    void Add(HealthSnapshot snapshot);

    Task<HealthSnapshot?> GetLatestAsync(Guid assessmentId, CancellationToken cancellationToken);

    /// <summary>Latest snapshot per assessment, for list views (one query, no N+1).</summary>
    Task<IReadOnlyDictionary<Guid, HealthSnapshot>> GetLatestForAsync(
        IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken);

    Task<HealthSnapshot?> GetByRunAsync(Guid runId, CancellationToken cancellationToken);
}

public interface IInventoryRepository
{
    void Add(InventorySnapshot snapshot);

    Task<IReadOnlyList<InventorySnapshot>> GetByRunAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Latest snapshot per language for the assessment (one run's worth).</summary>
    Task<IReadOnlyList<InventorySnapshot>> GetLatestByAssessmentAsync(Guid assessmentId, CancellationToken cancellationToken);

    /// <summary>Latest snapshots per language for many assessments (portfolio view, one query).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<InventorySnapshot>>> GetLatestForAsync(IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken);
}

public interface IScanJobQueue
{
    void Enqueue(ScanJob job);

    /// <summary>Atomically leases the next claimable job (queued, or expired lease) — SKIP LOCKED semantics.</summary>
    Task<ScanJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>True while a job for the assessment is queued, leased or running (prevents duplicate runs).</summary>
    Task<bool> HasActiveJobAsync(Guid assessmentId, CancellationToken cancellationToken);

    /// <summary>Active job state per assessment (Queued / Leased / Running); absent when idle. One query for list views.</summary>
    Task<IReadOnlyDictionary<Guid, ScanJobState>> GetActiveJobStatesAsync(
        IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken);

    /// <summary>Most recent jobs (optionally one state) for the queue view.</summary>
    Task<IReadOnlyList<ScanJob>> ListRecentAsync(int take, ScanJobState? state, CancellationToken cancellationToken);

    Task<ScanJob?> GetAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
