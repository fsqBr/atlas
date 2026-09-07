using Atlas.Application.Assessments;

namespace Atlas.Application.Findings;

/// <summary>
/// Recomputes an assessment's health score outside a run (a triage snapshot):
/// shared by every handler whose decision changes which findings are open.
/// </summary>
internal static class AssessmentHealthRecompute
{
    public static async Task RecomputeAsync(
        IFindingRepository findings,
        IInventoryRepository inventory,
        IHealthRepository health,
        IUnitOfWork unitOfWork,
        Guid assessmentId,
        Guid tenantId,
        CancellationToken cancellationToken)
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
