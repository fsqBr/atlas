using Atlas.Application.Portfolio;

namespace Atlas.Application.Tests;

public class BenchmarkTests
{
    [Fact]
    public void Percentiles_interpolate_and_handle_tiny_samples()
    {
        int[] sorted = [10, 20, 30, 40, 50];
        Assert.Equal(20, Benchmark.Percentile(sorted, 25));
        Assert.Equal(30, Benchmark.Percentile(sorted, 50));
        Assert.Equal(40, Benchmark.Percentile(sorted, 75));
        Assert.Equal(10, Benchmark.Percentile(sorted, 0));
        Assert.Equal(50, Benchmark.Percentile(sorted, 100));

        Assert.Equal(25, Benchmark.Percentile([10, 40], 50));
        Assert.Equal(42, Benchmark.Percentile([42], 75));
        Assert.Equal(0, Benchmark.Percentile([], 50));
    }

    [Fact]
    public void Percentile_rank_uses_midpoint_ranking_so_ties_share_the_middle()
    {
        int[] all = [21, 47, 56, 89];
        Assert.Equal(12, Benchmark.PercentileRank(all, 21));
        Assert.Equal(62, Benchmark.PercentileRank(all, 56));
        Assert.Equal(88, Benchmark.PercentileRank(all, 89));
        Assert.Equal(0, Benchmark.PercentileRank([], 50));

        // A uniform estate has no best or worst: everyone sits in the middle, nobody reads "100".
        Assert.Equal(50, Benchmark.PercentileRank([45, 45, 45], 45));
        Assert.Equal(50, Benchmark.PercentileRank([42], 42));
    }

    [Fact]
    public void Describe_reports_quartiles_best_and_worst()
    {
        var d = Benchmark.Describe("Security", [88, 12, 100, 45]);

        Assert.Equal("Security", d.Name);
        Assert.Equal(4, d.Count);
        Assert.Equal(100, d.Best);
        Assert.Equal(12, d.Worst);
        Assert.Equal(66.5, d.P50);
        Assert.True(d.P25 <= d.P50 && d.P50 <= d.P75);
    }
}
