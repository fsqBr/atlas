using Atlas.Domain.Findings;

namespace Atlas.Domain.Health;

public sealed record HealthInput(string RuleId, FindingCategory Category, Severity Severity);

public sealed record HealthContributor(string RuleId, int Count, double Points);

public sealed record HealthDimension(
    string Name,
    double Weight,
    int Score,
    double Penalty,
    IReadOnlyList<HealthContributor> Contributors);

public sealed record HealthResult(
    string ModelVersion,
    int Score,
    RiskLevel RiskLevel,
    IReadOnlyList<HealthDimension> Dimensions,
    string Explanation);

public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// Atlas Health Score model v1. Deterministic and versioned:
/// the same findings always yield the same score, and a model change is a new
/// version, never an in-place edit — so historical comparisons stay honest.
///
/// Per open finding, penalty points by severity (Critical 15, High 8, Medium 3,
/// Low 1, Informational 0) accumulate per dimension. Penalties are normalized to
/// a 10-project estate (scale = 10 / max(10, projects)) so a 200-project estate
/// is not punished for its size alone. Dimension score = 100 − min(100, penalty
/// × scale). Overall = weighted mean of dimensions.
/// </summary>
public static class HealthScoreCalculator
{
    public const string ModelVersion = "health.v1";

    private static readonly IReadOnlyDictionary<Severity, double> SeverityPoints = new Dictionary<Severity, double>
    {
        [Severity.Critical] = 15,
        [Severity.High] = 8,
        [Severity.Medium] = 3,
        [Severity.Low] = 1,
        [Severity.Informational] = 0,
    };

    private static readonly IReadOnlyList<(string Name, double Weight, FindingCategory[] Categories)> DimensionSpecs =
    [
        ("Security", 0.30, [FindingCategory.Security, FindingCategory.Secrets, FindingCategory.Data]),
        ("Modernization", 0.25, [FindingCategory.Modernization]),
        ("Dependencies", 0.15, [FindingCategory.Dependencies]),
        ("Architecture", 0.15, [FindingCategory.Architecture]),
        ("Quality", 0.15, [FindingCategory.Quality, FindingCategory.Code]),
    ];

    public static HealthResult Compute(IReadOnlyCollection<HealthInput> openFindings, int projectCount)
    {
        var scale = 10.0 / Math.Max(10, projectCount);
        var dimensions = new List<HealthDimension>();

        foreach (var (name, weight, categories) in DimensionSpecs)
        {
            var relevant = openFindings.Where(f => categories.Contains(f.Category)).ToList();
            var penalty = relevant.Sum(f => SeverityPoints[f.Severity]);
            var score = (int)Math.Round(Math.Max(0, 100 - Math.Min(100, penalty * scale)));

            var contributors = relevant
                .GroupBy(f => f.RuleId, StringComparer.Ordinal)
                .Select(g => new HealthContributor(g.Key, g.Count(), g.Sum(f => SeverityPoints[f.Severity]) * scale))
                .OrderByDescending(c => c.Points)
                .ThenBy(c => c.RuleId, StringComparer.Ordinal)
                .Take(5)
                .ToList();

            dimensions.Add(new HealthDimension(name, weight, score, Math.Round(penalty * scale, 1), contributors));
        }

        var overall = (int)Math.Round(dimensions.Sum(d => d.Weight * d.Score));
        var level = overall switch
        {
            < 40 => RiskLevel.Critical,
            < 60 => RiskLevel.High,
            < 80 => RiskLevel.Medium,
            _ => RiskLevel.Low,
        };

        var explanation =
            $"{ModelVersion}: {openFindings.Count} open finding(s) across {Math.Max(1, projectCount)} project(s); " +
            $"penalties normalized to a 10-project estate (scale {scale:0.###}); " +
            "weights Security 30%, Modernization 25%, Dependencies 15%, Architecture 15%, Quality 15%.";

        return new HealthResult(ModelVersion, overall, level, dimensions, explanation);
    }
}
