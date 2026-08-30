namespace Atlas.Domain.Modernization;

public sealed record RoadmapWorkItem(string Key, int Quantity);

public sealed record RoadmapPhase(
    string Key,
    int Order,
    double EffortShare,
    Range EffortHours,
    Range DurationMonths,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<RoadmapWorkItem> WorkItems);

public sealed record Roadmap(string ModelVersion, ModernizationStrategy Strategy, IReadOnlyList<RoadmapPhase> Phases);

/// <summary>
/// Phases of the design notes, generated from evidence: a phase only appears when
/// the findings call for it, its effort is a share of the strategy's estimate and
/// dependencies are explicit. roadmap.v1 assumes phases run sequentially with
/// the same team (no parallelism credit) — stated as such in the report.
/// </summary>
public static class RoadmapBuilder
{
    public const string ModelVersion = "roadmap.v1";

    public static Roadmap Build(ModernizationProfile p, CostEstimate estimate)
    {
        var strategy = estimate.Strategy;
        var phases = new List<(string Key, double Weight, string[] DependsOn, List<RoadmapWorkItem> Items)>();

        phases.Add(("phase.baseline", 0.04, [], [new("work.inventory", p.Projects), new("work.health-baseline", 1)]));

        var securityItems = new List<RoadmapWorkItem>();
        AddIf(securityItems, "work.fix-critical", p.CriticalSecurity);
        AddIf(securityItems, "work.fix-high", p.HighSecurity);
        AddIf(securityItems, "work.rotate-secrets", p.SecretsFound);
        AddIf(securityItems, "work.update-vulnerable-packages", p.VulnerablePackages);
        if (securityItems.Count > 0)
        {
            var securityHours = estimate.Breakdown.Where(b => b.Key.StartsWith("effort.security", StringComparison.Ordinal) || b.Key is "effort.secrets" or "effort.vulnerable-packages").Sum(b => b.Hours);
            phases.Add(("phase.security", Math.Clamp(securityHours / Math.Max(1, estimate.EffortHours.Likely), 0.05, 0.35), ["phase.baseline"], securityItems));
        }

        if (p.TestDeficit && strategy != ModernizationStrategy.KeepStabilize)
        {
            var items = new List<RoadmapWorkItem> { new("work.characterization-tests", Math.Max(1, p.ProjectsWithoutTests)) };
            if (p.CoverageLineRate is null)
            {
                items.Add(new RoadmapWorkItem("work.coverage-pipeline", 1));
            }

            phases.Add(("phase.tests", 0.15, ["phase.baseline"], items));
        }

        if (strategy != ModernizationStrategy.KeepStabilize && strategy != ModernizationStrategy.FullRewrite
            && (p.LegacyFrameworkProjects > 0 || p.LegacyProjectFormat > 0 || p.PrerequisiteBlockers > 0))
        {
            var items = new List<RoadmapWorkItem>();
            AddIf(items, "work.sdk-style", p.LegacyProjectFormat);
            AddIf(items, "work.package-reference", p.Blockers.Where(b => b.RuleId.EndsWith("mb-002", StringComparison.Ordinal)).Sum(b => b.Occurrences));
            AddIf(items, "work.target-framework", p.LegacyFrameworkProjects);
            AddIf(items, "work.medium-blockers", p.MediumBlockers);
            phases.Add(("phase.foundation", strategy == ModernizationStrategy.UpgradeInPlace ? 0.45 : 0.20, DependsOnPresent(phases, "phase.security", "phase.tests", "phase.baseline"), items));
        }

        if (strategy is ModernizationStrategy.Incremental or ModernizationStrategy.Strangler or ModernizationStrategy.PartialRewrite or ModernizationStrategy.FullRewrite)
        {
            var items = new List<RoadmapWorkItem>();
            AddIf(items, "work.high-blockers", p.HighBlockers);
            if (p.HasWebUi)
            {
                items.Add(new RoadmapWorkItem("work.web-ui", 1));
            }

            items.Add(new RoadmapWorkItem(strategy switch
            {
                ModernizationStrategy.Strangler => "work.strangler-slices",
                ModernizationStrategy.PartialRewrite => "work.rewrite-bounded-context",
                ModernizationStrategy.FullRewrite => "work.rewrite-all",
                _ => "work.migrate-projects",
            }, Math.Max(1, strategy == ModernizationStrategy.PartialRewrite ? p.ProjectsWithBlockers : p.Projects)));
            phases.Add(("phase.domain", 0.35, DependsOnPresent(phases, "phase.foundation", "phase.tests", "phase.security", "phase.baseline"), items));
        }
        else if (strategy == ModernizationStrategy.UpgradeInPlace && p.HighBlockers > 0)
        {
            phases.Add(("phase.domain", 0.20, DependsOnPresent(phases, "phase.foundation", "phase.baseline"), [new("work.high-blockers", p.HighBlockers)]));
        }

        if ((p.HasEntityFramework6 || p.HasWcfRemotingOrMsmq) && strategy != ModernizationStrategy.KeepStabilize)
        {
            var items = new List<RoadmapWorkItem>();
            if (p.HasEntityFramework6)
            {
                items.Add(new RoadmapWorkItem("work.ef-core", 1));
            }

            if (p.HasWcfRemotingOrMsmq)
            {
                items.Add(new RoadmapWorkItem("work.integration-protocols", 1));
            }

            phases.Add(("phase.data-integration", 0.12, DependsOnPresent(phases, "phase.domain", "phase.foundation", "phase.baseline"), items));
        }

        if (strategy is ModernizationStrategy.Strangler or ModernizationStrategy.PartialRewrite or ModernizationStrategy.FullRewrite)
        {
            phases.Add(("phase.retirement", 0.05, DependsOnPresent(phases, "phase.data-integration", "phase.domain"), [new("work.decommission", 1)]));
        }

        // Normalize shares to 1.0 so the phases add up to the estimate.
        var total = phases.Sum(ph => ph.Weight);
        var result = new List<RoadmapPhase>();
        var order = 0;
        foreach (var (key, weight, dependsOn, items) in phases)
        {
            var share = weight / total;
            result.Add(new RoadmapPhase(
                key,
                order++,
                Math.Round(share, 3),
                new Range(Round(estimate.EffortHours.Optimistic * share), Round(estimate.EffortHours.Likely * share), Round(estimate.EffortHours.Conservative * share)),
                new Range(Math.Round(estimate.DurationMonths.Optimistic * share, 1), Math.Round(estimate.DurationMonths.Likely * share, 1), Math.Round(estimate.DurationMonths.Conservative * share, 1)),
                dependsOn,
                items));
        }

        return new Roadmap(ModelVersion, strategy, result);
    }

    private static string[] DependsOnPresent(List<(string Key, double Weight, string[] DependsOn, List<RoadmapWorkItem> Items)> phases, params string[] candidates)
    {
        // The nearest present predecessor(s): security and tests can run side by side, so both are listed when present.
        var present = candidates.Where(c => phases.Any(p => p.Key == c)).ToList();
        if (present.Count == 0)
        {
            return [];
        }

        if (present[0] is "phase.security" or "phase.tests")
        {
            return present.Where(p => p is "phase.security" or "phase.tests").ToArray();
        }

        return [present[0]];
    }

    private static void AddIf(List<RoadmapWorkItem> items, string key, int quantity)
    {
        if (quantity > 0)
        {
            items.Add(new RoadmapWorkItem(key, quantity));
        }
    }

    private static double Round(double hours) => Math.Round(hours / 10) * 10;
}
