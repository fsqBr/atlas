using Atlas.Domain.Health;

namespace Atlas.Domain.Modernization;

public enum ModernizationStrategy
{
    KeepStabilize,
    UpgradeInPlace,
    Incremental,
    Strangler,
    PartialRewrite,
    FullRewrite,
}

/// <summary>
/// One strategy weighed against the evidence. Text is carried as keys
/// (rationale, prerequisites, blockers, benefits) and rendered per language by
/// the presentation layer — the engine never produces prose.
/// </summary>
public sealed record StrategyEvaluation(
    ModernizationStrategy Strategy,
    int FitScore,
    RiskLevel Risk,
    IReadOnlyList<string> Rationale,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Benefits);

public sealed record ModernizationAssessment(
    string ModelVersion,
    ModernizationStrategy Recommended,
    IReadOnlyList<StrategyEvaluation> Strategies);

/// <summary>
/// Compares the six modernization strategies of the design notes on evidence
/// (framework generation, blockers, test posture, size, coupling, security debt)
/// and recommends the best fit. modernization.v1: fit scores are transparent
/// additive rules, so a reader can see why a strategy won.
/// </summary>
public static class ModernizationAnalyzer
{
    public const string ModelVersion = "modernization.v1";

    public static ModernizationAssessment Analyze(ModernizationProfile p)
    {
        var evaluations = new List<StrategyEvaluation>
        {
            Keep(p), Upgrade(p), Incremental(p), Strangler(p), PartialRewrite(p), FullRewrite(p),
        };

        var recommended = evaluations
            .OrderByDescending(e => e.FitScore)
            .ThenBy(e => e.Risk)
            .ThenBy(e => e.Strategy)
            .First()
            .Strategy;

        return new ModernizationAssessment(ModelVersion, recommended, evaluations);
    }

    private static StrategyEvaluation Keep(ModernizationProfile p)
    {
        var fit = 20;
        var why = new List<string>();
        if (p.LegacyFrameworkProjects == 0 && p.UnknownShare < 0.5)
        {
            fit += 55;
            why.Add("rationale.no-legacy-frameworks");
        }
        else
        {
            fit -= 20;
            why.Add("rationale.legacy-frameworks-present");
        }

        if (p.CriticalSecurity + p.HighSecurity == 0)
        {
            fit += 10;
        }
        else
        {
            why.Add("rationale.security-debt");
        }

        if (p.Blockers.Count == 0)
        {
            fit += 10;
        }

        var risk = p.LegacyFrameworkProjects > 0 ? RiskLevel.High : p.CriticalSecurity + p.HighSecurity > 0 ? RiskLevel.Medium : RiskLevel.Low;
        return new StrategyEvaluation(ModernizationStrategy.KeepStabilize, Clamp(fit), risk, why,
            Prerequisites: ["prereq.security-remediation", "prereq.dependency-updates"],
            Blockers: p.LegacyFrameworkProjects > 0 ? ["blocker.eol-runtime"] : [],
            Benefits: ["benefit.lowest-cost", "benefit.no-functional-change"]);
    }

    private static StrategyEvaluation Upgrade(ModernizationProfile p)
    {
        var fit = 30;
        var why = new List<string>();
        if (p.NoUpgradePathProjects > 0)
        {
            fit -= Math.Min(30, 10 * p.NoUpgradePathProjects);
            why.Add("rationale.ui-no-upgrade-path");
        }

        if (p.DesktopProjects > 0 && p.NoUpgradePathProjects == 0)
        {
            fit += 5;
            why.Add("rationale.desktop-upgrade-path");
        }

        if (p.LegacyFrameworkProjects > 0)
        {
            fit += 25;
            why.Add("rationale.legacy-frameworks-present");
        }

        if (p.HighBlockers == 0)
        {
            fit += 30;
            why.Add("rationale.no-hard-blockers");
        }
        else
        {
            // Distinct rules set the base, occurrences push toward the cap: one WCF rule hit in 45
            // projects is a far worse upgrade story than a single isolated hit.
            fit -= Math.Min(40, 10 * p.Blockers.Count(b => b.Weight == BlockerWeight.High) + p.HighBlockers / 5 * 5);
            why.Add("rationale.hard-blockers-present");
        }

        if (p.HasWebUi)
        {
            fit -= 15;
            why.Add("rationale.web-ui-rewrite");
        }

        if (p.LinesOfCode < 150_000)
        {
            fit += 10;
            why.Add("rationale.small-estate");
        }

        if (p.TestDeficit)
        {
            fit -= 10;
            why.Add("rationale.test-deficit");
        }

        var risk = p.HighBlockers > 0 ? RiskLevel.High : p.TestDeficit ? RiskLevel.Medium : RiskLevel.Low;
        var blockers = p.Blockers.Where(b => b.Weight == BlockerWeight.High).Select(b => "blocker." + b.RuleId[(b.RuleId.LastIndexOf('.') + 1)..]).ToList();
        return new StrategyEvaluation(ModernizationStrategy.UpgradeInPlace, Clamp(fit), risk, why,
            Prerequisites: Prereqs(p, "prereq.sdk-style", "prereq.package-reference"),
            Blockers: blockers,
            Benefits: ["benefit.supported-runtime", "benefit.performance", "benefit.same-architecture"]);
    }

    private static StrategyEvaluation Incremental(ModernizationProfile p)
    {
        var fit = 40;
        var why = new List<string>();
        if (p.LegacyFrameworkProjects > 0)
        {
            fit += 15;
        }

        if (p.HighBlockers > 0)
        {
            fit += 20;
            why.Add("rationale.hard-blockers-present");
        }

        if (p.Projects is >= 5 and <= 60)
        {
            fit += 15;
            why.Add("rationale.medium-estate");
        }

        if (!p.TestDeficit)
        {
            fit += 10;
            why.Add("rationale.tests-enable-refactoring");
        }
        else
        {
            why.Add("rationale.test-deficit");
        }

        if (p.ArchitectureCycles > 0)
        {
            fit += 5;
            why.Add("rationale.coupling");
        }

        var risk = p.TestDeficit ? RiskLevel.Medium : RiskLevel.Low;
        return new StrategyEvaluation(ModernizationStrategy.Incremental, Clamp(fit), risk, why,
            Prerequisites: Prereqs(p, "prereq.sdk-style", "prereq.characterization-tests", "prereq.netstandard-bridge"),
            Blockers: p.Blockers.Where(b => b.Weight == BlockerWeight.High).Select(b => "blocker." + b.RuleId[(b.RuleId.LastIndexOf('.') + 1)..]).ToList(),
            Benefits: ["benefit.continuous-delivery", "benefit.risk-spread", "benefit.supported-runtime"]);
    }

    private static StrategyEvaluation Strangler(ModernizationProfile p)
    {
        var fit = 20;
        var why = new List<string>();
        if (p.LinesOfCode >= 300_000 || p.Projects > 40)
        {
            fit += 30;
            why.Add("rationale.large-estate");
        }

        if (p.HasWebUi || p.HasWcfRemotingOrMsmq)
        {
            fit += 25;
            why.Add("rationale.edge-replaceable");
        }

        if (p.HighBlockers >= 3)
        {
            fit += 10;
            why.Add("rationale.hard-blockers-present");
        }

        if (p.Projects < 5)
        {
            fit -= 25;
            why.Add("rationale.small-estate");
        }

        var risk = p.LinesOfCode >= 300_000 ? RiskLevel.High : RiskLevel.Medium;
        return new StrategyEvaluation(ModernizationStrategy.Strangler, Clamp(fit), risk, why,
            Prerequisites: ["prereq.facade-routing", "prereq.characterization-tests", "prereq.observability"],
            Blockers: p.HasWcfRemotingOrMsmq ? ["blocker.integration-protocols"] : [],
            Benefits: ["benefit.parallel-run", "benefit.risk-spread", "benefit.new-architecture"]);
    }

    private static StrategyEvaluation PartialRewrite(ModernizationProfile p)
    {
        var fit = 15;
        var why = new List<string>();
        if (p.HighBlockers > 0 && p.BlockedProjectShare > 0 && p.BlockedProjectShare <= 0.35)
        {
            fit += 45;
            why.Add("rationale.blockers-concentrated");
        }
        else if (p.HighBlockers > 0)
        {
            fit += 10;
            why.Add("rationale.blockers-spread");
        }

        if (p.HasWebUi)
        {
            fit += 15;
            why.Add("rationale.web-ui-rewrite");
        }

        if (p.TestDeficit)
        {
            fit -= 10;
            why.Add("rationale.test-deficit");
        }

        return new StrategyEvaluation(ModernizationStrategy.PartialRewrite, Clamp(fit), RiskLevel.High, why,
            Prerequisites: ["prereq.boundary-definition", "prereq.characterization-tests"],
            Blockers: [],
            Benefits: ["benefit.remove-hard-blockers", "benefit.keep-stable-core"]);
    }

    private static StrategyEvaluation FullRewrite(ModernizationProfile p)
    {
        var fit = 5;
        var why = new List<string>();
        if (p.LinesOfCode > 0 && p.LinesOfCode < 40_000 && p.HighBlockers > 0)
        {
            fit += 45;
            why.Add("rationale.small-estate-many-blockers");
        }

        if (p.HighBlockers >= 4 && p.BlockedProjectShare > 0.6)
        {
            fit += 20;
            why.Add("rationale.blockers-spread");
        }

        if (p.LinesOfCode >= 150_000)
        {
            fit -= 30;
            why.Add("rationale.large-estate");
        }

        if (p.TestDeficit)
        {
            fit -= 15;
            why.Add("rationale.rewrite-without-tests");
        }

        var risk = p.TestDeficit ? RiskLevel.Critical : RiskLevel.High;
        return new StrategyEvaluation(ModernizationStrategy.FullRewrite, Clamp(fit), risk, why,
            Prerequisites: ["prereq.business-rule-inventory", "prereq.characterization-tests", "prereq.parallel-run-plan"],
            Blockers: p.LinesOfCode >= 150_000 ? ["blocker.size"] : [],
            Benefits: ["benefit.new-architecture", "benefit.remove-hard-blockers"]);
    }

    private static IReadOnlyList<string> Prereqs(ModernizationProfile p, params string[] keys)
    {
        var list = new List<string>();
        foreach (var key in keys)
        {
            switch (key)
            {
                case "prereq.sdk-style" when p.LegacyProjectFormat == 0:
                case "prereq.package-reference" when p.Blockers.All(b => !b.RuleId.EndsWith("mb-002", StringComparison.Ordinal)):
                    continue;
                default:
                    list.Add(key);
                    break;
            }
        }

        return list;
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 100);
}
