using Atlas.Application.Assessments;
using Atlas.Domain.Findings;
using Atlas.Application.Tenants;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Findings;

public interface ISuppressionPolicyRepository
{
    void Add(SuppressionPolicy policy);

    void Remove(SuppressionPolicy policy);

    Task<SuppressionPolicy?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Tenant-wide policies plus the assessment's own.</summary>
    Task<IReadOnlyList<SuppressionPolicy>> ListForAssessmentAsync(Guid assessmentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SuppressionPolicy>> ListAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates/removes suppression policies and applies a new policy to the
/// assessment's existing open findings right away (they are suppressed with the
/// policy as reason), then recomputes the health score.
/// </summary>
public sealed class SuppressionPolicyHandler(
    ISuppressionPolicyRepository policies,
    IAssessmentRepository assessments,
    IFindingRepository findings,
    ISuppressionRepository suppressions,
    IInventoryRepository inventory,
    IHealthRepository health,
    IUnitOfWork unitOfWork,
    ILogger<SuppressionPolicyHandler> logger,
    ITenantContext tenant)
{
    private const int MaxFindings = 20_000;

    public async Task<(SuppressionPolicy Policy, int Applied)> CreateAsync(
        Guid? assessmentId, string rulePattern, string? pathGlob, string reason, string author, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken)
    {
        var policy = new SuppressionPolicy(Guid.NewGuid(), tenant.Require(), assessmentId, rulePattern, pathGlob, reason, author, expiresAtUtc);
        policies.Add(policy);

        var applied = 0;
        var touched = new List<Guid>();
        if (assessmentId is { } id)
        {
            applied = await ApplyToExistingAsync(id, policy, cancellationToken);
            if (applied > 0)
            {
                touched.Add(id);
            }
        }
        else
        {
            // Tenant-wide policy: same contract as an assessment-scoped one — existing open
            // findings are suppressed now, not only on each assessment's next run.
            foreach (var candidate in await assessments.ListIdsAsync(cancellationToken))
            {
                var count = await ApplyToExistingAsync(candidate, policy, cancellationToken);
                applied += count;
                if (count > 0)
                {
                    touched.Add(candidate);
                }
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Suppression policy {Policy} created by {Author}; {Applied} open finding(s) suppressed.", policy.Describe(), author, applied);

        foreach (var recompute in touched)
        {
            await RecomputeHealthAsync(recompute, cancellationToken);
        }

        return (policy, applied);
    }

    public async Task<bool> DeleteAsync(Guid policyId, CancellationToken cancellationToken)
    {
        var policy = await policies.GetAsync(policyId, cancellationToken);
        if (policy is null)
        {
            return false;
        }

        policies.Remove(policy);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Drops candidates a policy covers before reconciliation (so they resolve and never reappear).</summary>
    public static IReadOnlyList<Atlas.Scanner.Abstractions.FindingCandidate> Filter(
        IReadOnlyList<Atlas.Scanner.Abstractions.FindingCandidate> candidates, IReadOnlyList<SuppressionPolicy> active, out int dropped)
    {
        // Expired waivers stop filtering: their findings come back on this run.
        var live = active.Where(p => p.IsActive(DateTimeOffset.UtcNow)).ToList();
        if (live.Count == 0)
        {
            dropped = 0;
            return candidates;
        }

        var kept = candidates.Where(c => !live.Any(p => p.Matches(c.RuleId, c.Evidence.FilePath))).ToList();
        dropped = candidates.Count - kept.Count;
        return kept;
    }

    private async Task<int> ApplyToExistingAsync(Guid assessmentId, SuppressionPolicy policy, CancellationToken cancellationToken)
    {
        var page = await findings.ListAsync(assessmentId, 0, MaxFindings, cancellationToken, new FindingFilter(Status: FindingStatus.Open));
        var regressed = await findings.ListAsync(assessmentId, 0, MaxFindings, cancellationToken, new FindingFilter(Status: FindingStatus.Regressed));
        var applied = 0;
        foreach (var item in page.Items.Concat(regressed.Items))
        {
            if (!policy.Matches(item.Finding.RuleId, item.Latest?.Evidence.FilePath))
            {
                continue;
            }

            item.Finding.Suppress();
            suppressions.Add(new FindingSuppression(
                Guid.NewGuid(), item.Finding, SuppressionKind.Suppressed, $"Policy {policy.Describe()}: {policy.Reason}", policy.Author, policy.ExpiresAtUtc));
            applied++;
        }

        return applied;
    }

    private async Task RecomputeHealthAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var open = await findings.ListOpenAsync(assessmentId, cancellationToken);
        var snapshots = await inventory.GetLatestByAssessmentAsync(assessmentId, cancellationToken);
        var latest = await health.GetLatestAsync(assessmentId, cancellationToken);
        if (latest is null)
        {
            return;
        }

        var projectCount = snapshots.Sum(s => s.ProjectCount);
        health.Add(HealthSnapshotFactory.Create(latest.TenantId, assessmentId, latest.CommitSha, open, projectCount, runId: null));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
