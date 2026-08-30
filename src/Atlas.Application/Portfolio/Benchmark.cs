namespace Atlas.Application.Portfolio;

/// <summary>Distribution of one score across the estate: quartiles plus best/worst.</summary>
public sealed record BenchmarkDimension(string Name, int Count, double P25, double P50, double P75, int Best, int Worst);

public sealed record PortfolioBenchmark(IReadOnlyList<BenchmarkDimension> Dimensions);

/// <summary>Percentiles over small samples (a portfolio has tens of assessments, not millions): linear interpolation.</summary>
public static class Benchmark
{
    public static double Percentile(IReadOnlyList<int> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            return 0;
        }

        if (sortedAscending.Count == 1)
        {
            return sortedAscending[0];
        }

        var rank = p / 100.0 * (sortedAscending.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sortedAscending.Count - 1);
        var fraction = rank - lower;
        return Math.Round(sortedAscending[lower] + (sortedAscending[upper] - sortedAscending[lower]) * fraction, 1);
    }

    /// <summary>Share of the estate at or below this score (0–100); 100 means best in the estate.</summary>
    public static int PercentileRank(IReadOnlyList<int> all, int score)
    {
        if (all.Count == 0)
        {
            return 0;
        }

        var atOrBelow = all.Count(s => s <= score);
        return (int)Math.Round(atOrBelow * 100.0 / all.Count);
    }

    public static BenchmarkDimension Describe(string name, IEnumerable<int> scores)
    {
        var sorted = scores.OrderBy(s => s).ToList();
        return new BenchmarkDimension(name, sorted.Count,
            Percentile(sorted, 25), Percentile(sorted, 50), Percentile(sorted, 75),
            sorted.Count == 0 ? 0 : sorted[^1], sorted.Count == 0 ? 0 : sorted[0]);
    }
}
