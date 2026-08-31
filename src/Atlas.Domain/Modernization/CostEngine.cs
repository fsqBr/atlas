namespace Atlas.Domain.Modernization;

/// <summary>
/// Every knob of cost.v1, with defaults. Versioned so estimates can be
/// recomputed and calibrated against real project outcomes.
/// </summary>
public sealed record CostParameters
{
    public const string SectionName = "Atlas:Cost";

    public string ModelVersion { get; init; } = "cost.v1";

    /// <summary>Base effort per 1,000 lines of code, by strategy.</summary>
    public double KeepHoursPerKloc { get; init; } = 1.5;
    public double UpgradeHoursPerKloc { get; init; } = 6;
    public double IncrementalHoursPerKloc { get; init; } = 14;
    public double StranglerHoursPerKloc { get; init; } = 18;
    public double RewriteHoursPerKloc { get; init; } = 40;

    /// <summary>Per blocker occurrence (project-level), for strategies that migrate rather than rewrite.</summary>
    public double PrerequisiteBlockerHours { get; init; } = 12;
    public double HighBlockerHours { get; init; } = 80;
    public double MediumBlockerHours { get; init; } = 32;

    public double CriticalSecurityHours { get; init; } = 16;
    public double HighSecurityHours { get; init; } = 6;
    public double MediumSecurityHours { get; init; } = 2;
    public double SecretHours { get; init; } = 4;
    public double VulnerablePackageHours { get; init; } = 3;

    public double NoTestsMultiplier { get; init; } = 1.30;
    public double LowCoverageMultiplier { get; init; } = 1.20;
    public double UnknownCoverageMultiplier { get; init; } = 1.10;
    public double HighComplexityMultiplier { get; init; } = 1.15;
    public double CouplingMultiplier { get; init; } = 1.10;

    public double OptimisticFactor { get; init; } = 0.75;
    public double ConservativeFactor { get; init; } = 1.5;
    public double LowConfidenceOptimisticFactor { get; init; } = 0.7;
    public double LowConfidenceConservativeFactor { get; init; } = 1.8;

    public int TeamSize { get; init; } = 4;
    public double ProductiveHoursPerDeveloperMonth { get; init; } = 130;
    public decimal HourlyRate { get; init; } = 180;
    public string Currency { get; init; } = "BRL";
}

public enum EstimateConfidence
{
    Low,
    Medium,
    High,
}

public sealed record Range(double Optimistic, double Likely, double Conservative);

public sealed record MoneyRange(decimal Optimistic, decimal Likely, decimal Conservative, string Currency);

/// <summary>A line of the effort breakdown: what drove hours, with the count it was applied to.</summary>
public sealed record EffortItem(string Key, double Hours, double Quantity);

/// <summary>An assumption the estimate rests on, as a key plus the value used (rendered per language).</summary>
public sealed record Assumption(string Key, string Value);

public sealed record CostEstimate(
    string ModelVersion,
    ModernizationStrategy Strategy,
    Range EffortHours,
    Range DurationMonths,
    MoneyRange Cost,
    EstimateConfidence Confidence,
    IReadOnlyList<EffortItem> Breakdown,
    IReadOnlyList<Assumption> Assumptions);

/// <summary>
/// Ranges, never fake precision: base effort from size, plus
/// blockers and security debt, times explicit multipliers; optimistic/likely/
/// conservative widen with low confidence. Every input is listed as an assumption.
/// </summary>
public static class CostEngine
{
    public static CostEstimate Estimate(ModernizationProfile p, ModernizationStrategy strategy, CostParameters c)
    {
        var breakdown = new List<EffortItem>();
        var kloc = p.LinesOfCode / 1000.0;

        var perKloc = strategy switch
        {
            ModernizationStrategy.KeepStabilize => c.KeepHoursPerKloc,
            ModernizationStrategy.UpgradeInPlace => c.UpgradeHoursPerKloc,
            ModernizationStrategy.Incremental => c.IncrementalHoursPerKloc,
            ModernizationStrategy.Strangler => c.StranglerHoursPerKloc,
            ModernizationStrategy.FullRewrite => c.RewriteHoursPerKloc,
            ModernizationStrategy.PartialRewrite => 0, // split below
            _ => c.IncrementalHoursPerKloc,
        };

        if (strategy == ModernizationStrategy.PartialRewrite)
        {
            var rewrittenShare = Math.Clamp(p.BlockedProjectShare == 0 ? 0.3 : p.BlockedProjectShare, 0.1, 0.7);
            breakdown.Add(new EffortItem("effort.rewrite-share", kloc * rewrittenShare * c.RewriteHoursPerKloc, Math.Round(kloc * rewrittenShare, 1)));
            breakdown.Add(new EffortItem("effort.upgrade-share", kloc * (1 - rewrittenShare) * c.UpgradeHoursPerKloc, Math.Round(kloc * (1 - rewrittenShare), 1)));
        }
        else
        {
            breakdown.Add(new EffortItem("effort.base", kloc * perKloc, Math.Round(kloc, 1)));
        }

        // Blockers: migrations pay for them; rewrites make them disappear (but still need analysis: 25%).
        var blockerFactor = strategy switch
        {
            ModernizationStrategy.KeepStabilize => 0,
            ModernizationStrategy.FullRewrite => 0.25,
            ModernizationStrategy.PartialRewrite => 0.5,
            _ => 1.0,
        };
        if (blockerFactor > 0)
        {
            AddIf(breakdown, "effort.blockers-prerequisite", p.PrerequisiteBlockers, c.PrerequisiteBlockerHours * blockerFactor);
            AddIf(breakdown, "effort.blockers-high", p.HighBlockers, c.HighBlockerHours * blockerFactor);
            AddIf(breakdown, "effort.blockers-medium", p.MediumBlockers, c.MediumBlockerHours * blockerFactor);
        }

        // Security debt is paid under every strategy.
        AddIf(breakdown, "effort.security-critical", p.CriticalSecurity, c.CriticalSecurityHours);
        AddIf(breakdown, "effort.security-high", p.HighSecurity, c.HighSecurityHours);
        AddIf(breakdown, "effort.security-medium", p.MediumSecurity, c.MediumSecurityHours);
        AddIf(breakdown, "effort.secrets", p.SecretsFound, c.SecretHours);
        AddIf(breakdown, "effort.vulnerable-packages", p.VulnerablePackages, c.VulnerablePackageHours);

        var subtotal = breakdown.Sum(b => b.Hours);
        var assumptions = new List<Assumption>
        {
            new("assumption.lines-of-code", p.LinesOfCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("assumption.hours-per-kloc", strategy == ModernizationStrategy.PartialRewrite
                ? $"{c.RewriteHoursPerKloc}/{c.UpgradeHoursPerKloc}"
                : perKloc.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        var multiplier = 1.0;
        if (strategy != ModernizationStrategy.KeepStabilize)
        {
            if (!p.HasTests)
            {
                multiplier *= c.NoTestsMultiplier;
                assumptions.Add(new Assumption("assumption.no-tests", c.NoTestsMultiplier.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
            }
            else if (p.CoverageLineRate is { } rate && rate < 0.3)
            {
                multiplier *= c.LowCoverageMultiplier;
                assumptions.Add(new Assumption("assumption.low-coverage", (rate * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"));
            }
            else if (p.CoverageLineRate is null)
            {
                multiplier *= c.UnknownCoverageMultiplier;
                assumptions.Add(new Assumption("assumption.unknown-coverage", c.UnknownCoverageMultiplier.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
            }

            if (p.AverageComplexity > 10)
            {
                multiplier *= c.HighComplexityMultiplier;
                assumptions.Add(new Assumption("assumption.high-complexity", p.AverageComplexity.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
            }

            if (p.ArchitectureCycles > 0 && strategy != ModernizationStrategy.FullRewrite)
            {
                multiplier *= c.CouplingMultiplier;
                assumptions.Add(new Assumption("assumption.coupling", p.ArchitectureCycles.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        var likely = Math.Max(subtotal * multiplier, strategy == ModernizationStrategy.KeepStabilize ? 8 : 40);
        var confidence = Confidence(p);
        var (optimisticFactor, conservativeFactor) = confidence == EstimateConfidence.Low
            ? (c.LowConfidenceOptimisticFactor, c.LowConfidenceConservativeFactor)
            : (c.OptimisticFactor, c.ConservativeFactor);

        var likelyRounded = Round(likely);
        // "Ranges, never fake precision": rounding to 10h must not collapse the band on small estates.
        var optimisticRounded = Math.Min(Round(likely * optimisticFactor), Math.Max(5, likelyRounded - 10));
        var conservativeRounded = Math.Max(Round(likely * conservativeFactor), likelyRounded + 10);
        var effort = new Range(optimisticRounded, likelyRounded, conservativeRounded);
        var monthlyCapacity = Math.Max(1, c.TeamSize) * c.ProductiveHoursPerDeveloperMonth;
        var duration = new Range(
            Math.Max(0.5, Math.Round(effort.Optimistic / monthlyCapacity, 1)),
            Math.Max(1, Math.Round(effort.Likely / monthlyCapacity, 1)),
            Math.Max(1, Math.Round(effort.Conservative / monthlyCapacity, 1)));
        var cost = new MoneyRange(
            Math.Round((decimal)effort.Optimistic * c.HourlyRate, 0),
            Math.Round((decimal)effort.Likely * c.HourlyRate, 0),
            Math.Round((decimal)effort.Conservative * c.HourlyRate, 0),
            c.Currency);

        assumptions.Add(new Assumption("assumption.team-size", c.TeamSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        assumptions.Add(new Assumption("assumption.hours-per-month", c.ProductiveHoursPerDeveloperMonth.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        assumptions.Add(new Assumption("assumption.hourly-rate", $"{c.HourlyRate.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} {c.Currency}"));
        assumptions.Add(new Assumption("assumption.range-factors", $"×{optimisticFactor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} / ×{conservativeFactor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}"));
        assumptions.Add(new Assumption("assumption.confidence", confidence.ToString()));

        return new CostEstimate(c.ModelVersion, strategy, effort, duration, cost, confidence, breakdown, assumptions);
    }

    private static EstimateConfidence Confidence(ModernizationProfile p)
    {
        if (p.LinesOfCode == 0 || p.Projects == 0 || p.UnknownShare > 0.3)
        {
            return EstimateConfidence.Low;
        }

        var symbols = string.Equals(p.Tier, "SyntacticWithSymbols", StringComparison.OrdinalIgnoreCase) || string.Equals(p.Tier, "Full", StringComparison.OrdinalIgnoreCase);
        return symbols && p.CoverageLineRate is not null && p.UnknownShare == 0 ? EstimateConfidence.High : EstimateConfidence.Medium;
    }

    private static void AddIf(List<EffortItem> items, string key, int quantity, double hoursEach)
    {
        if (quantity > 0 && hoursEach > 0)
        {
            items.Add(new EffortItem(key, quantity * hoursEach, quantity));
        }
    }

    private static double Round(double hours) => Math.Round(hours / 10) * 10;
}
