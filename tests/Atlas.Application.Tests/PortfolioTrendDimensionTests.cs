using Atlas.Application.Portfolio;

namespace Atlas.Application.Tests;

public class PortfolioTrendDimensionTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static CompletedRunPoint Run(Guid id, string date, int score, int open, Dictionary<string, int>? dims) =>
        new(id, DateTimeOffset.Parse(date + "T12:00:00Z"), score, open, dims);

    [Fact]
    public void Dimension_averages_follow_the_same_latest_per_assessment_sampling()
    {
        var runs = new List<CompletedRunPoint>
        {
            Run(A, "2026-08-01", 40, 10, new() { ["Security"] = 30, ["Quality"] = 50 }),
            Run(A, "2026-08-20", 60, 8, new() { ["Security"] = 70, ["Quality"] = 50 }),
            Run(B, "2026-08-10", 80, 2, new() { ["Security"] = 90 }),
        };

        var points = PortfolioTrend.Compute(runs, new DateOnly(2026, 8, 29), weeks: 5);
        var last = points[^1];

        Assert.NotNull(last.DimensionAverages);
        Assert.Equal(80, last.DimensionAverages!["Security"]);   // (70 + 90) / 2, A's latest run counts
        Assert.Equal(50, last.DimensionAverages["Quality"]);     // only A carries Quality — average over carriers

        var early = points[0];
        Assert.Equal(30, early.DimensionAverages!["Security"]);  // only A existed on Aug 1
    }

    [Fact]
    public void Points_without_dimension_data_expose_null()
    {
        var runs = new List<CompletedRunPoint> { Run(A, "2026-08-20", 75, 5, null) };
        var last = PortfolioTrend.Compute(runs, new DateOnly(2026, 8, 29), weeks: 2)[^1];
        Assert.Null(last.DimensionAverages);
    }
}
