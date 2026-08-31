using Atlas.Application.Findings;
using Atlas.Domain.Findings;
using Atlas.Domain.Modernization;

namespace Atlas.Application.Assessments;

/// <summary>Everything the modernization tab and the report section need, computed from persisted facts.</summary>
public sealed record ModernizationPlan(
    ModernizationProfile Profile,
    ModernizationAssessment Assessment,
    IReadOnlyList<CostEstimate> Estimates,
    Roadmap Roadmap)
{
    public CostEstimate Recommended => Estimates.First(e => e.Strategy == Assessment.Recommended);
}

/// <summary>
/// Reduces the assessment's open findings and inventory to a ModernizationProfile
/// and runs the three engines. Deterministic: same findings and parameters, same
/// plan — so the plan is recomputed on demand instead of stored.
/// </summary>
public sealed class ModernizationPlanBuilder(
    IAssessmentRepository assessments,
    IFindingRepository findings,
    IInventoryRepository inventory,
    ITenantCostProfileRepository costProfiles,
    CostParameters parameters)
{
    private const int MaxFindings = 10_000;

    public async Task<ModernizationPlan?> BuildAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var snapshots = await inventory.GetLatestByAssessmentAsync(assessmentId, cancellationToken);
        if (snapshots.Count == 0)
        {
            return null;
        }

        // The tenant's market overrides the deployment defaults: a US estate is estimated at US$
        // rates, not at converted BRL — an hourly rate is a market fact, not an FX quote.
        var owner = await assessments.GetAsync(assessmentId, cancellationToken);
        var costProfile = owner is null ? null : await costProfiles.GetForTenantAsync(owner.TenantId, cancellationToken);
        var effectiveParameters = costProfile is null
            ? parameters
            : parameters with
            {
                Currency = costProfile.Currency,
                HourlyRate = costProfile.HourlyRate,
                TeamSize = costProfile.TeamSize ?? parameters.TeamSize,
            };

        var page = await findings.ListAsync(assessmentId, 0, MaxFindings, cancellationToken,
            new FindingFilter(Status: FindingStatus.Open));
        var regressed = await findings.ListAsync(assessmentId, 0, MaxFindings, cancellationToken,
            new FindingFilter(Status: FindingStatus.Regressed));

        var facts = page.Items.Concat(regressed.Items)
            .Select(i => new FindingFact(
                i.Finding.RuleId, i.Finding.Severity, i.Finding.Category,
                i.Latest?.Evidence.FilePath, FindingLocalizer.Data(i.Latest)))
            .ToList();

        var projects = snapshots
            .SelectMany(InventorySnapshotFactory.ReadProjects)
            .Select(p => new ProjectSummary(p.Name, p.TargetFramework, p.IsSdkStyle, p.UiFramework))
            .ToList();
        var primary = snapshots.OrderByDescending(s => s.TotalLines).First();
        var estate = new EstateFacts(
            snapshots.Sum(s => s.TotalLines),
            snapshots.Sum(s => s.FileCount),
            snapshots.Sum(s => s.TypeCount),
            snapshots.Sum(s => s.MethodCount),
            snapshots.Max(s => s.MaxCyclomaticComplexity),
            primary.AverageCyclomaticComplexity,
            primary.SymbolResolutionRate,
            primary.TierAchieved,
            projects);

        var profile = ModernizationProfile.From(facts, estate);
        var assessment = ModernizationAnalyzer.Analyze(profile);
        var estimates = assessment.Strategies.Select(s => CostEngine.Estimate(profile, s.Strategy, effectiveParameters)).ToList();
        var roadmap = RoadmapBuilder.Build(profile, estimates.First(e => e.Strategy == assessment.Recommended));
        return new ModernizationPlan(profile, assessment, estimates, roadmap);
    }
}
