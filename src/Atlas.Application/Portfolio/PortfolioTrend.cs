namespace Atlas.Application.Portfolio;

/// <summary>One health snapshot, reduced to what the trend needs — no new storage.</summary>
public sealed record CompletedRunPoint(
    Guid AssessmentId,
    DateTimeOffset FinishedAtUtc,
    int? HealthScore,
    int? OpenFindings,
    IReadOnlyDictionary<string, int>? Dimensions = null);

/// <summary>The estate at one sampled date: the latest snapshot of each assessment on or before it.</summary>
public sealed record PortfolioTrendPoint(
    DateOnly Date,
    double? AverageScore,
    int OpenFindings,
    int Assessed,
    IReadOnlyDictionary<string, double>? DimensionAverages = null);

/// <summary>
/// Weekly history of the portfolio, recomputed on demand from the runs that were
/// already persisted: for each sampled week, every assessment counts with its
/// most recent completed run up to that date. Deterministic and retroactive —
/// runs executed before this feature existed appear in the chart.
/// </summary>
public static class PortfolioTrend
{
    public const int DefaultWeeks = 26;
    public const int MaxWeeks = 104;

    public static IReadOnlyList<PortfolioTrendPoint> Compute(IReadOnlyList<CompletedRunPoint> runs, DateOnly today, int weeks = DefaultWeeks)
    {
        weeks = Math.Clamp(weeks, 2, MaxWeeks);
        var usable = runs.Where(r => r.HealthScore is not null || r.OpenFindings is not null).ToList();
        if (usable.Count == 0)
        {
            return [];
        }

        var first = DateOnly.FromDateTime(usable.Min(r => r.FinishedAtUtc).UtcDateTime);
        var dates = Enumerable.Range(0, weeks)
            .Select(i => today.AddDays(-7 * (weeks - 1 - i)))
            .Where(d => d >= first) // no empty points before the first run ever
            .ToList();
        if (dates.Count == 0 || dates[^1] != today)
        {
            dates.Add(today);
        }

        var byAssessment = usable
            .GroupBy(r => r.AssessmentId)
            .Select(g => g.OrderBy(r => r.FinishedAtUtc).ToList())
            .ToList();

        var points = new List<PortfolioTrendPoint>(dates.Count);
        foreach (var date in dates)
        {
            var cutoff = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
            var latest = byAssessment
                .Select(history => history.LastOrDefault(r => r.FinishedAtUtc.UtcDateTime <= cutoff))
                .Where(r => r is not null)
                .Select(r => r!)
                .ToList();
            var scored = latest.Where(r => r.HealthScore is not null).ToList();
            // Per-dimension averages across the assessments that carry that dimension.
            var dimensions = latest
                .Where(r => r.Dimensions is not null)
                .SelectMany(r => r.Dimensions!)
                .GroupBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Math.Round(g.Average(kv => (double)kv.Value), 1), StringComparer.Ordinal);
            points.Add(new PortfolioTrendPoint(
                date,
                scored.Count == 0 ? null : Math.Round(scored.Average(r => (double)r.HealthScore!), 1),
                latest.Sum(r => r.OpenFindings ?? 0),
                scored.Count,
                dimensions.Count == 0 ? null : dimensions));
        }

        return points;
    }
}
