using Atlas.Domain.Findings;
using Atlas.Domain.Health;

namespace Atlas.Domain.Tests.Health;

public class HealthScoreCalculatorTests
{
    private static HealthInput F(FindingCategory category, Severity severity, string rule = "r") => new(rule, category, severity);

    [Fact]
    public void Clean_estate_scores_100_low_risk()
    {
        var result = HealthScoreCalculator.Compute([], projectCount: 3);

        Assert.Equal(100, result.Score);
        Assert.Equal(RiskLevel.Low, result.RiskLevel);
        Assert.Equal(HealthScoreCalculator.ModelVersion, result.ModelVersion);
        Assert.All(result.Dimensions, d => Assert.Equal(100, d.Score));
        Assert.Equal(1.0, result.Dimensions.Sum(d => d.Weight), 3);
    }

    [Fact]
    public void Penalties_follow_severity_points_and_land_in_the_right_dimension()
    {
        var findings = new[]
        {
            F(FindingCategory.Security, Severity.Critical), // 15
            F(FindingCategory.Security, Severity.High),     // 8
            F(FindingCategory.Secrets, Severity.Medium),    // 3  -> Security 26
            F(FindingCategory.Modernization, Severity.Low), // 1  -> Modernization 1
            F(FindingCategory.Quality, Severity.Informational), // 0
        };

        var result = HealthScoreCalculator.Compute(findings, projectCount: 5);

        Assert.Equal(74, result.Dimensions.Single(d => d.Name == "Security").Score);
        Assert.Equal(99, result.Dimensions.Single(d => d.Name == "Modernization").Score);
        Assert.Equal(100, result.Dimensions.Single(d => d.Name == "Quality").Score);
        Assert.Equal(100, result.Dimensions.Single(d => d.Name == "Dependencies").Score);
        // 0.30*74 + 0.25*99 + 0.15*100*3 = 22.2 + 24.75 + 45 = 91.95 -> 92
        Assert.Equal(92, result.Score);
    }

    [Fact]
    public void Large_estates_are_normalized_not_punished_for_size()
    {
        var findings = Enumerable.Range(0, 20).Select(_ => F(FindingCategory.Security, Severity.High)).ToList(); // 160 points

        var small = HealthScoreCalculator.Compute(findings, projectCount: 5);   // scale 1   -> 0
        var large = HealthScoreCalculator.Compute(findings, projectCount: 200); // scale .05 -> 100-8 = 92

        Assert.Equal(0, small.Dimensions.Single(d => d.Name == "Security").Score);
        Assert.Equal(92, large.Dimensions.Single(d => d.Name == "Security").Score);
    }

    [Theory]
    [InlineData(0, RiskLevel.Critical)]
    [InlineData(39, RiskLevel.Critical)]
    [InlineData(40, RiskLevel.High)]
    [InlineData(59, RiskLevel.High)]
    [InlineData(60, RiskLevel.Medium)]
    [InlineData(79, RiskLevel.Medium)]
    [InlineData(80, RiskLevel.Low)]
    public void Risk_levels_follow_thresholds(int targetScore, RiskLevel expected)
    {
        // n Low findings (1 point each) in every dimension on a small estate: each dimension scores 100 - n,
        // so the weighted overall is exactly 100 - n.
        var n = 100 - targetScore;
        var categories = new[]
        {
            FindingCategory.Security, FindingCategory.Modernization, FindingCategory.Dependencies,
            FindingCategory.Architecture, FindingCategory.Quality,
        };
        var findings = categories.SelectMany(c => Enumerable.Range(0, n).Select(_ => F(c, Severity.Low))).ToList();

        var result = HealthScoreCalculator.Compute(findings, projectCount: 1);

        Assert.Equal(targetScore, result.Score);
        Assert.Equal(expected, result.RiskLevel);
    }

    [Fact]
    public void Contributors_explain_the_penalty_by_rule()
    {
        var findings = new[]
        {
            F(FindingCategory.Modernization, Severity.High, "mb"),
            F(FindingCategory.Modernization, Severity.High, "mb"),
            F(FindingCategory.Modernization, Severity.Low, "legacy"),
        };

        var modernization = HealthScoreCalculator.Compute(findings, 3).Dimensions.Single(d => d.Name == "Modernization");

        Assert.Equal(17, modernization.Penalty);
        var top = modernization.Contributors[0];
        Assert.Equal("mb", top.RuleId);
        Assert.Equal(2, top.Count);
        Assert.Equal(16, top.Points);
    }

    [Fact]
    public void Same_inputs_same_score()
    {
        var findings = new[] { F(FindingCategory.Architecture, Severity.Medium), F(FindingCategory.Dependencies, Severity.High) };

        var first = HealthScoreCalculator.Compute(findings, 12);
        var second = HealthScoreCalculator.Compute(findings, 12);

        Assert.Equal(first.Score, second.Score);
        Assert.Equal(first.RiskLevel, second.RiskLevel);
        Assert.Equal(first.Dimensions.Select(d => (d.Name, d.Score, d.Penalty)), second.Dimensions.Select(d => (d.Name, d.Score, d.Penalty)));
        Assert.Equal(first.Explanation, second.Explanation);
    }
}
