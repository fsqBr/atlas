using Atlas.Application.Findings;
using Atlas.Domain.Findings;
using Atlas.Domain.Modernization;

namespace Atlas.Application.Assessments;

/// <summary>Everything the modernization tab and the report section need, computed from persisted facts.</summary>
public sealed record ModernizationPlan(
    ModernizationProfile Profile,
    ModernizationAssessment Assessment,
    IReadOnlyList<CostEstimate> Estimates,
    Roadmap Roadmap,
    SavingsEstimate? Savings = null)
{
    public CostEstimate Recommended => Estimates.First(e => e.Strategy == Assessment.Recommended);

    /// <summary>Longer than this, "payback" is a meaningless figure to show a client (it reads as broken output).</summary>
    private const double PaybackHorizonMonths = 120; // 10 years

    /// <summary>
    /// Fraction of the legacy estate a strategy actually retires — full savings only materialize
    /// when the whole legacy estate is replaced. Incremental/partial strategies keep much of it
    /// running, so charging them the FULL annual savings makes their payback look too optimistic.
    /// These shares are an explicit modeling assumption (savings.v1), not a measured figure.
    /// </summary>
    private static double LegacyRetiredShare(ModernizationStrategy strategy) => strategy switch
    {
        ModernizationStrategy.KeepStabilize => 0.0,
        ModernizationStrategy.UpgradeInPlace => 1.0,
        ModernizationStrategy.FullRewrite => 1.0,
        ModernizationStrategy.Strangler => 0.7,
        ModernizationStrategy.PartialRewrite => 0.6,
        ModernizationStrategy.Incremental => 0.5,
        _ => 0.5,
    };

    /// <summary>Months for the strategy's likely cost to pay for itself out of the annual savings it
    /// actually captures (scaled by the legacy share it retires); null when there is nothing to save,
    /// for KeepStabilize, or when the payback is beyond a 10-year horizon (shown as "—").</summary>
    public double? PaybackMonths(CostEstimate estimate)
    {
        if (Savings is not { AnnualTotal: > 0 } savings)
        {
            return null;
        }

        var share = LegacyRetiredShare(estimate.Strategy);
        if (share <= 0)
        {
            return null;
        }

        var capturedAnnual = (double)savings.AnnualTotal * share;
        var months = Math.Round((double)estimate.Cost.Likely / (capturedAnnual / 12), 1);
        return months > PaybackHorizonMonths ? null : months;
    }
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
        // The deployment savings knobs are market rates in the deployment's currency. When the
        // tenant's currency matches, use them (with any per-tenant overrides). When it differs, they
        // cannot be FX-relabeled — but a tenant that supplies its OWN savings rates gets savings in
        // its currency; otherwise savings stay hidden rather than mislabeled.
        var sameCurrency = costProfile is null || string.Equals(costProfile.Currency, parameters.Currency, StringComparison.OrdinalIgnoreCase);
        SavingsEstimate? savings = null;
        if (sameCurrency)
        {
            savings = SavingsEngine.Estimate(profile, effectiveParameters with
            {
                WindowsHostingPerLegacyAppYear = costProfile?.WindowsHostingPerLegacyAppYear ?? parameters.WindowsHostingPerLegacyAppYear,
                ExtendedSupportPerLegacyAppYear = costProfile?.ExtendedSupportPerLegacyAppYear ?? parameters.ExtendedSupportPerLegacyAppYear,
                SqlServerSavingsPerYear = costProfile?.SqlServerSavingsPerYear ?? parameters.SqlServerSavingsPerYear,
            });
        }
        else if (costProfile is not null
            && (costProfile.WindowsHostingPerLegacyAppYear is not null
                || costProfile.ExtendedSupportPerLegacyAppYear is not null
                || costProfile.SqlServerSavingsPerYear is not null))
        {
            // Foreign-currency tenant with its own rates: a missing rate is 0 (that line is hidden).
            savings = SavingsEngine.Estimate(profile, effectiveParameters with
            {
                WindowsHostingPerLegacyAppYear = costProfile.WindowsHostingPerLegacyAppYear ?? 0,
                ExtendedSupportPerLegacyAppYear = costProfile.ExtendedSupportPerLegacyAppYear ?? 0,
                SqlServerSavingsPerYear = costProfile.SqlServerSavingsPerYear ?? 0,
            });
        }

        return new ModernizationPlan(profile, assessment, estimates, roadmap, savings);
    }
}
