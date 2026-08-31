using Atlas.Domain.Findings;
using Atlas.Domain.Modernization;

namespace Atlas.Domain.Tests.Modernization;

public class SavingsEngineTests
{
    private static ModernizationProfile Profile(int legacy, bool ef6 = false) => new(
        LinesOfCode: 120_000, Projects: 20, Types: 800, Methods: 4000, MaxComplexity: 30, AverageComplexity: 4.2,
        LegacyFrameworkProjects: legacy, ModernFrameworkProjects: 20 - legacy, UnknownFrameworkProjects: 0,
        LegacyProjectFormat: legacy, Blockers: [], ProjectsWithBlockers: 0,
        CriticalSecurity: 0, HighSecurity: 0, MediumSecurity: 0, SecretsFound: 0, VulnerablePackages: 0,
        HasTests: true, CoverageLineRate: 0.6, ProjectsWithoutTests: 0, ArchitectureCycles: 0, HighFanOut: 0,
        HasWebUi: true, HasWcfRemotingOrMsmq: false, HasEntityFramework6: ef6, SymbolResolutionRate: 0.9, Tier: "SyntacticWithSymbols");

    [Fact]
    public void Savings_scale_with_the_legacy_estate_and_use_the_tenant_currency()
    {
        var parameters = new CostParameters { Currency = "USD", WindowsHostingPerLegacyAppYear = 4000, ExtendedSupportPerLegacyAppYear = 1000, SqlServerSavingsPerYear = 10000 };
        var savings = SavingsEngine.Estimate(Profile(legacy: 5, ef6: true), parameters)!;

        Assert.Equal("USD", savings.Currency);
        Assert.Equal(20000, savings.Items.Single(i => i.Key == "saving.windows-hosting").AnnualAmount);
        Assert.Equal(5000, savings.Items.Single(i => i.Key == "saving.extended-support").AnnualAmount);
        Assert.Equal(10000, savings.Items.Single(i => i.Key == "saving.sql-server").AnnualAmount);
        Assert.Equal(35000, savings.AnnualTotal); // density is deliberately NOT modeled: hosting already saves 100%
        Assert.Contains(savings.Assumptions, a => a.Key == "assumption.saving-scope" && a.Value == "5");
    }

    [Fact]
    public void A_modern_estate_has_no_savings_section()
    {
        Assert.Null(SavingsEngine.Estimate(Profile(legacy: 0), new CostParameters()));
    }

    [Fact]
    public void No_sql_saving_without_entity_framework_6()
    {
        var savings = SavingsEngine.Estimate(Profile(legacy: 3, ef6: false), new CostParameters())!;
        Assert.DoesNotContain(savings.Items, i => i.Key == "saving.sql-server");
    }
}
