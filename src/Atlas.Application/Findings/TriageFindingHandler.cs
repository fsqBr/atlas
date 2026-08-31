using Atlas.Application.Assessments;
using Atlas.Domain.Findings;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Findings;

public enum TriageAction
{
    Suppress,
    FalsePositive,
    Reopen,
}

/// <summary>
/// Human triage of a finding: suppress (accepted risk), mark false positive, or
/// reopen. Each decision is an auditable record and the health score is
/// recomputed right away as a triage snapshot (no run), so the number a
/// consultant shows matches the findings they actually stand behind.
/// </summary>
public sealed class TriageFindingHandler(
    IFindingRepository findings,
    ISuppressionRepository suppressions,
    IInventoryRepository inventory,
    IHealthRepository health,
    IUnitOfWork unitOfWork,
    ILogger<TriageFindingHandler> logger)
{
    public async Task<Finding> HandleAsync(
        Guid assessmentId, Guid findingId, TriageAction action, string? reason, string? author, CancellationToken cancellationToken, DateTimeOffset? expiresAtUtc = null)
    {
        var finding = await findings.GetAsync(findingId, cancellationToken);
        if (finding is null || finding.AssessmentId != assessmentId)
        {
            throw new KeyNotFoundException($"Finding {findingId} not found in assessment {assessmentId}.");
        }

        var by = string.IsNullOrWhiteSpace(author) ? "unknown" : author.Trim();

        switch (action)
        {
            case TriageAction.Suppress:
            case TriageAction.FalsePositive:
                // Always revoke the previous active row: after an expiry sweep the finding is Open
                // but its stale suppression may still be active — never keep two active rows.
                (await suppressions.GetActiveAsync(finding.Id, cancellationToken))?.Revoke(by);

                var kind = action == TriageAction.Suppress ? SuppressionKind.Suppressed : SuppressionKind.FalsePositive;
                suppressions.Add(new FindingSuppression(
                    Guid.NewGuid(), finding, kind, reason ?? string.Empty, by,
                    kind == SuppressionKind.Suppressed ? expiresAtUtc : null));
                if (kind == SuppressionKind.Suppressed)
                {
                    finding.Suppress();
                }
                else
                {
                    finding.MarkFalsePositive();
                }

                break;

            case TriageAction.Reopen:
                (await suppressions.GetActiveAsync(finding.Id, cancellationToken))?.Revoke(by);
                finding.Reopen();
                break;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await RecomputeHealthAsync(assessmentId, finding.TenantId, cancellationToken);

        logger.LogInformation("Finding {FindingId} {Action} by {Author}.", finding.Id, action, by);
        return finding;
    }

    private async Task RecomputeHealthAsync(Guid assessmentId, Guid tenantId, CancellationToken cancellationToken)
    {
        var open = await findings.ListOpenAsync(assessmentId, cancellationToken);
        var latestInventory = await inventory.GetLatestByAssessmentAsync(assessmentId, cancellationToken);
        var latestHealth = await health.GetLatestAsync(assessmentId, cancellationToken);

        health.Add(HealthSnapshotFactory.Create(
            tenantId, assessmentId, latestHealth?.CommitSha, open,
            latestInventory.Sum(i => i.ProjectCount), runId: null));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
