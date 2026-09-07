using Atlas.Application.Assessments;
using Atlas.Domain.Findings;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Findings;

/// <summary>A finding created by a rule major bump whose superseded finding carries a live waiver.</summary>
public sealed record MigratableWaiver(Finding Finding, FindingSuppression Predecessor);

public sealed record WaiverMigrationResult(int Migrated, int Skipped, IReadOnlyList<Guid> MigratedFindingIds);

/// <summary>
/// Waiver migration after a rule major bump. A bump gives every match a new
/// fingerprint, so the old finding resolves and a new Open one appears — on purpose: a
/// rule that changed meaning demands re-triage. But the predecessor's waiver must not
/// vanish silently: this handler lists the new findings whose predecessor carries an
/// active, unexpired waiver, and — only on an explicit human request — re-issues that
/// waiver against the new finding as a fresh auditable suppression naming its origin.
/// </summary>
public sealed class WaiverMigrationHandler(
    IAssessmentRepository assessments,
    IFindingRepository findings,
    ISuppressionRepository suppressions,
    IInventoryRepository inventory,
    IHealthRepository health,
    IUnitOfWork unitOfWork,
    ILogger<WaiverMigrationHandler> logger)
{
    public async Task<IReadOnlyList<MigratableWaiver>?> ListAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        if (await assessments.GetAsync(assessmentId, cancellationToken) is null)
        {
            return null;
        }

        var candidates = await findings.ListWithPredecessorAsync(assessmentId, cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        var byPredecessor = await ActiveWaiversByFingerprintAsync(assessmentId, cancellationToken);
        return candidates
            .Where(f => byPredecessor.ContainsKey(f.PredecessorFingerprint!))
            .Select(f => new MigratableWaiver(f, byPredecessor[f.PredecessorFingerprint!]))
            .OrderByDescending(m => m.Finding.Severity)
            .ThenBy(m => m.Finding.RuleId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Migrates the waivers of the selected findings (all migratable ones when <paramref name="findingIds"/>
    /// is null or empty). Requested findings that are not migratable — already triaged, no predecessor, or
    /// the predecessor's waiver expired/revoked meanwhile — are counted as skipped, never guessed at.
    /// </summary>
    public async Task<WaiverMigrationResult?> MigrateAsync(
        Guid assessmentId,
        IReadOnlyCollection<Guid>? findingIds,
        string author,
        CancellationToken cancellationToken)
    {
        var migratable = await ListAsync(assessmentId, cancellationToken);
        if (migratable is null)
        {
            return null;
        }

        var requested = findingIds is { Count: > 0 } ? findingIds.ToHashSet() : null;
        var selected = requested is null
            ? migratable
            : migratable.Where(m => requested.Contains(m.Finding.Id)).ToList();
        var skipped = requested?.Count - selected.Count ?? 0;

        var by = string.IsNullOrWhiteSpace(author) ? "unknown" : author.Trim();
        var migrated = new List<Guid>();
        Guid? tenantId = null;

        foreach (var (finding, predecessor) in selected)
        {
            // Never keep two active rows on one finding (same invariant as triage).
            (await suppressions.GetActiveAsync(finding.Id, cancellationToken))?.Revoke(by);

            var expiry = predecessor.ExpiresAtUtc is { } e && e > DateTimeOffset.UtcNow ? e : (DateTimeOffset?)null;
            suppressions.Add(new FindingSuppression(
                Guid.NewGuid(), finding, predecessor.Kind,
                Truncate($"{predecessor.Reason} [waiver migrated from superseded finding {predecessor.Fingerprint[..12]} after rule major bump]"),
                by,
                predecessor.Kind == SuppressionKind.Suppressed ? expiry : null));

            if (predecessor.Kind == SuppressionKind.Suppressed)
            {
                finding.Suppress();
            }
            else
            {
                finding.MarkFalsePositive();
            }

            migrated.Add(finding.Id);
            tenantId = finding.TenantId;
        }

        if (migrated.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await AssessmentHealthRecompute.RecomputeAsync(
                findings, inventory, health, unitOfWork, assessmentId, tenantId!.Value, cancellationToken);
            logger.LogInformation(
                "Migrated {Count} waiver(s) in assessment {AssessmentId} by {Author} ({Skipped} skipped).",
                migrated.Count, assessmentId, by, skipped);
        }

        return new WaiverMigrationResult(migrated.Count, skipped, migrated);
    }

    /// <summary>Newest active, unexpired waiver per fingerprint — an expired waiver is a lapsed decision, not a migratable one.</summary>
    private async Task<Dictionary<string, FindingSuppression>> ActiveWaiversByFingerprintAsync(
        Guid assessmentId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return (await suppressions.ListByAssessmentAsync(assessmentId, cancellationToken))
            .Where(s => s.IsActive && (s.ExpiresAtUtc is null || s.ExpiresAtUtc > now))
            .GroupBy(s => s.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CreatedAtUtc).First(), StringComparer.Ordinal);
    }

    private static string Truncate(string reason) => reason.Length <= 2000 ? reason : reason[..2000];
}
