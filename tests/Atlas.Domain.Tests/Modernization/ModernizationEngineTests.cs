using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Modernization;

namespace Atlas.Domain.Tests.Modernization;

public class ModernizationEngineTests
{
    private static FindingFact Fact(string ruleId, Severity severity, FindingCategory category, string? file = null, Dictionary<string, string>? data = null) =>
        new(ruleId, severity, category, file, data);

    private static EstateFacts Estate(long loc, params (string Name, string? Tfm, bool Sdk)[] projects) =>
        new(loc, (int)(loc / 200), (int)(loc / 400), (int)(loc / 60), 25, 6.5, 0.9, "SyntacticWithSymbols",
            projects.Select(p => new ProjectSummary(p.Name, p.Tfm, p.Sdk)).ToList());

    private static readonly IReadOnlyList<FindingFact> LegacyWebFormsFindings =
    [
        Fact("dependency.migration-blocker.mb-001", Severity.High, FindingCategory.Modernization, "Web/Web.csproj"),
        Fact("dependency.migration-blocker.mb-002", Severity.High, FindingCategory.Modernization, "Web/Web.csproj"),
        Fact("dependency.migration-blocker.mb-003", Severity.High, FindingCategory.Modernization, "Web/Web.csproj"),
        Fact("dependency.migration-blocker.mb-006", Severity.Medium, FindingCategory.Modernization, "Data/Data.csproj"),
        Fact("dependency.migration-blocker.mb-007", Severity.High, FindingCategory.Modernization, "Services/Services.csproj"),
        Fact("dependency.framework.end-of-life", Severity.High, FindingCategory.Modernization, "Web/Web.csproj"),
        Fact("security.sql.concatenation", Severity.Critical, FindingCategory.Security, "Data/Repo.cs"),
        Fact("security.debug.enabled", Severity.High, FindingCategory.Security, "Web/web.config"),
        Fact("secrets.connection-string-password", Severity.High, FindingCategory.Secrets, "Web/web.config"),
        Fact("dependency.package.vulnerable", Severity.High, FindingCategory.Dependencies, "Web/packages.config"),
        Fact("dependency.package.vulnerable", Severity.Critical, FindingCategory.Dependencies, "Web/packages.config"),
        Fact("quality.tests.none", Severity.Medium, FindingCategory.Quality),
        Fact("architecture.cycle.project", Severity.Medium, FindingCategory.Architecture),
    ];

    private static readonly EstateFacts LegacyEstate = Estate(120_000,
        ("Web", "v4.5.2", false), ("Data", "v4.5.2", false), ("Services", "v4.5.2", false), ("Core", "v4.5.2", false));

    [Fact]
    public void Profile_reduces_findings_and_inventory_to_evidence()
    {
        var p = ModernizationProfile.From(LegacyWebFormsFindings, LegacyEstate);

        Assert.Equal(4, p.LegacyFrameworkProjects);
        Assert.Equal(0, p.ModernFrameworkProjects);
        Assert.Equal(4, p.LegacyProjectFormat);
        Assert.Equal(2, p.PrerequisiteBlockers);
        Assert.Equal(2, p.HighBlockers);
        Assert.Equal(1, p.MediumBlockers);
        Assert.Equal(3, p.ProjectsWithBlockers);
        Assert.Equal(1, p.CriticalSecurity);
        Assert.Equal(1, p.HighSecurity);
        Assert.Equal(1, p.SecretsFound);
        Assert.Equal(2, p.VulnerablePackages);
        Assert.False(p.HasTests);
        Assert.True(p.TestDeficit);
        Assert.True(p.HasWebUi);
        Assert.True(p.HasWcfRemotingOrMsmq);
        Assert.True(p.HasEntityFramework6);
        Assert.Equal(1, p.ArchitectureCycles);
    }

    [Theory]
    [InlineData("v4.5.2", true)]
    [InlineData("net48", true)]
    [InlineData("net472", true)]
    [InlineData("net35", true)]
    [InlineData("net8.0", false)]
    [InlineData("net10.0", false)]
    [InlineData("netstandard2.0", false)]
    [InlineData("netcoreapp3.1", false)]
    [InlineData(null, false)]
    public void Legacy_framework_detection(string? tfm, bool legacy) => Assert.Equal(legacy, ModernizationProfile.IsLegacyFramework(tfm));

    [Fact]
    public void Legacy_webforms_estate_is_not_told_to_keep_or_upgrade_blindly()
    {
        var p = ModernizationProfile.From(LegacyWebFormsFindings, LegacyEstate);
        var a = ModernizationAnalyzer.Analyze(p);

        Assert.Equal(6, a.Strategies.Count);
        Assert.DoesNotContain(a.Recommended, new[] { ModernizationStrategy.KeepStabilize, ModernizationStrategy.FullRewrite });
        var keep = a.Strategies.Single(s => s.Strategy == ModernizationStrategy.KeepStabilize);
        Assert.Equal(RiskLevel.High, keep.Risk);
        Assert.Contains("blocker.eol-runtime", keep.Blockers);
        var upgrade = a.Strategies.Single(s => s.Strategy == ModernizationStrategy.UpgradeInPlace);
        Assert.Contains("blocker.mb-003", upgrade.Blockers);
        Assert.Contains("prereq.sdk-style", upgrade.Prerequisites);
        Assert.True(a.Strategies.Single(s => s.Strategy == a.Recommended).FitScore >= upgrade.FitScore);
        Assert.Equal(RiskLevel.Critical, a.Strategies.Single(s => s.Strategy == ModernizationStrategy.FullRewrite).Risk);
    }

    [Fact]
    public void Modern_clean_estate_is_told_to_keep()
    {
        var estate = Estate(60_000, ("Api", "net8.0", true), ("Domain", "net8.0", true), ("Tests", "net8.0", true));
        var a = ModernizationAnalyzer.Analyze(ModernizationProfile.From([], estate));

        Assert.Equal(ModernizationStrategy.KeepStabilize, a.Recommended);
        Assert.Equal(RiskLevel.Low, a.Strategies.Single(s => s.Strategy == ModernizationStrategy.KeepStabilize).Risk);
    }

    [Fact]
    public void Legacy_without_hard_blockers_prefers_upgrade_in_place()
    {
        var findings = new[]
        {
            Fact("dependency.migration-blocker.mb-001", Severity.High, FindingCategory.Modernization, "A/A.csproj"),
            Fact("dependency.framework.end-of-life", Severity.High, FindingCategory.Modernization, "A/A.csproj"),
            Fact("quality.coverage.summary", Severity.Informational, FindingCategory.Quality, null, new() { ["lineRate"] = "0.62" }),
        };
        var a = ModernizationAnalyzer.Analyze(ModernizationProfile.From(findings, Estate(30_000, ("A", "v4.7.2", false), ("A.Tests", "v4.7.2", false))));

        Assert.Equal(ModernizationStrategy.UpgradeInPlace, a.Recommended);
    }

    [Fact]
    public void Cost_ranges_are_ordered_rounded_and_explained()
    {
        var p = ModernizationProfile.From(LegacyWebFormsFindings, LegacyEstate);
        var c = new CostParameters();
        var e = CostEngine.Estimate(p, ModernizationStrategy.Incremental, c);

        Assert.True(e.EffortHours.Optimistic < e.EffortHours.Likely && e.EffortHours.Likely < e.EffortHours.Conservative);
        Assert.Equal(0, e.EffortHours.Likely % 10);
        Assert.True(e.DurationMonths.Likely >= 1);
        Assert.Equal((decimal)e.EffortHours.Likely * c.HourlyRate, e.Cost.Likely);
        Assert.Equal("BRL", e.Cost.Currency);
        Assert.Contains(e.Breakdown, b => b.Key == "effort.base");
        Assert.Contains(e.Breakdown, b => b.Key == "effort.blockers-high" && b.Quantity == 2);
        Assert.Contains(e.Breakdown, b => b.Key == "effort.security-critical");
        Assert.Contains(e.Assumptions, a => a.Key == "assumption.no-tests");
        Assert.Contains(e.Assumptions, a => a.Key == "assumption.team-size" && a.Value == "4");
        Assert.Equal(EstimateConfidence.Medium, e.Confidence); // symbols, but no coverage data

        // Base: 120 KLOC × 14h = 1680; blockers 2×12 + 2×80 + 1×32 = 216; security 16+6+4+6 = 32; ×1.30 (no tests) ×1.10 (cycles)
        var expected = Math.Round((1680 + 216 + 32) * 1.30 * 1.10 / 10) * 10;
        Assert.Equal(expected, e.EffortHours.Likely);
    }

    [Fact]
    public void Rewrite_pays_for_size_not_for_blockers_and_keep_pays_only_security()
    {
        var p = ModernizationProfile.From(LegacyWebFormsFindings, LegacyEstate);
        var c = new CostParameters();

        var keep = CostEngine.Estimate(p, ModernizationStrategy.KeepStabilize, c);
        Assert.DoesNotContain(keep.Breakdown, b => b.Key.StartsWith("effort.blockers", StringComparison.Ordinal));
        Assert.DoesNotContain(keep.Assumptions, a => a.Key == "assumption.no-tests");

        var rewrite = CostEngine.Estimate(p, ModernizationStrategy.FullRewrite, c);
        Assert.True(rewrite.EffortHours.Likely > CostEngine.Estimate(p, ModernizationStrategy.UpgradeInPlace, c).EffortHours.Likely);
        Assert.Equal(120 * c.RewriteHoursPerKloc, rewrite.Breakdown.First(b => b.Key == "effort.base").Hours, 0.01);
        // Blockers cost a quarter under a rewrite (analysis only).
        Assert.Equal(2 * c.HighBlockerHours * 0.25, rewrite.Breakdown.First(b => b.Key == "effort.blockers-high").Hours, 0.01);
        Assert.DoesNotContain(rewrite.Assumptions, a => a.Key == "assumption.coupling");
    }

    [Fact]
    public void Low_confidence_widens_the_range()
    {
        var unknownEstate = Estate(50_000, ("A", null, false), ("B", null, false), ("C", "v4.8", false));
        var p = ModernizationProfile.From([], unknownEstate);
        var e = CostEngine.Estimate(p, ModernizationStrategy.UpgradeInPlace, new CostParameters());

        Assert.Equal(EstimateConfidence.Low, e.Confidence);
        Assert.Equal(Math.Round(e.EffortHours.Likely * 1.8 / 10) * 10, e.EffortHours.Conservative);
    }

    [Fact]
    public void Roadmap_phases_follow_the_evidence_and_add_up()
    {
        var p = ModernizationProfile.From(LegacyWebFormsFindings, LegacyEstate);
        var e = CostEngine.Estimate(p, ModernizationStrategy.Incremental, new CostParameters());
        var r = RoadmapBuilder.Build(p, e);

        var keys = r.Phases.Select(ph => ph.Key).ToList();
        Assert.Equal(["phase.baseline", "phase.security", "phase.tests", "phase.foundation", "phase.domain", "phase.data-integration"], keys);
        Assert.Equal(1.0, r.Phases.Sum(ph => ph.EffortShare), 2);
        Assert.Equal(["phase.baseline"], r.Phases.Single(ph => ph.Key == "phase.security").DependsOn);
        Assert.Equal(["phase.security", "phase.tests"], r.Phases.Single(ph => ph.Key == "phase.foundation").DependsOn);
        Assert.Contains(r.Phases.Single(ph => ph.Key == "phase.security").WorkItems, w => w.Key == "work.rotate-secrets");
        Assert.Contains(r.Phases.Single(ph => ph.Key == "phase.data-integration").WorkItems, w => w.Key == "work.ef-core");
        Assert.Contains(r.Phases.Single(ph => ph.Key == "phase.domain").WorkItems, w => w.Key == "work.web-ui");
        Assert.True(Math.Abs(r.Phases.Sum(ph => ph.EffortHours.Likely) - e.EffortHours.Likely) <= 10 * r.Phases.Count);
    }

    [Fact]
    public void Clean_estate_roadmap_is_baseline_only()
    {
        var estate = Estate(60_000, ("Api", "net8.0", true));
        var p = ModernizationProfile.From([], estate);
        var e = CostEngine.Estimate(p, ModernizationStrategy.KeepStabilize, new CostParameters());
        var r = RoadmapBuilder.Build(p, e);

        Assert.Equal(["phase.baseline"], r.Phases.Select(ph => ph.Key));
        Assert.Equal(1.0, r.Phases[0].EffortShare);
    }
}
