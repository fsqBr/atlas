using Atlas.Application.Portfolio;

namespace Atlas.Application.Tests;

public class PortfolioTrendTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static CompletedRunPoint Run(Guid id, string date, int? score, int? open) =>
        new(id, DateTimeOffset.Parse(date + "T12:00:00Z"), score, open);

    [Fact]
    public void Each_week_counts_every_assessment_with_its_latest_run_up_to_that_date()
    {
        var runs = new List<CompletedRunPoint>
        {
            Run(A, "2026-08-01", 40, 100),
            Run(A, "2026-08-20", 60, 80),  // A improves mid-window
            Run(B, "2026-08-12", 80, 10),  // B joins later
        };

        var points = PortfolioTrend.Compute(runs, new DateOnly(2026, 8, 29), weeks: 5);

        Assert.Equal(5, points.Count);
        Assert.Equal(new DateOnly(2026, 8, 1), points[0].Date);
        Assert.Equal(new DateOnly(2026, 8, 29), points[^1].Date);

        Assert.Equal(1, points[0].Assessed);          // only A exists on Aug 1
        Assert.Equal(40, points[0].AverageScore);
        Assert.Equal(100, points[0].OpenFindings);

        var aug15 = points.Single(p => p.Date == new DateOnly(2026, 8, 15));
        Assert.Equal(2, aug15.Assessed);              // B joined on the 12th, A still at 40
        Assert.Equal(60, aug15.AverageScore);         // (40 + 80) / 2
        Assert.Equal(110, aug15.OpenFindings);

        var last = points[^1];
        Assert.Equal(70, last.AverageScore);          // (60 + 80) / 2 after A's second run
        Assert.Equal(90, last.OpenFindings);
    }

    [Fact]
    public void Points_before_the_first_run_are_dropped_and_today_is_always_sampled()
    {
        var runs = new List<CompletedRunPoint> { Run(A, "2026-08-27", 75, 5) };

        var points = PortfolioTrend.Compute(runs, new DateOnly(2026, 8, 29), weeks: 26);

        Assert.Single(points);
        Assert.Equal(new DateOnly(2026, 8, 29), points[0].Date);
        Assert.Equal(75, points[0].AverageScore);
    }

    [Fact]
    public void Runs_without_any_signal_are_ignored_and_unscored_runs_still_count_findings()
    {
        Assert.Empty(PortfolioTrend.Compute([], new DateOnly(2026, 8, 29)));
        Assert.Empty(PortfolioTrend.Compute([new CompletedRunPoint(A, DateTimeOffset.UtcNow, null, null)], new DateOnly(2026, 8, 29)));

        var runs = new List<CompletedRunPoint> { Run(A, "2026-08-20", null, 30), Run(B, "2026-08-20", 90, 2) };
        var last = PortfolioTrend.Compute(runs, new DateOnly(2026, 8, 29), weeks: 2)[^1];
        Assert.Equal(1, last.Assessed);          // only B carries a score
        Assert.Equal(90, last.AverageScore);
        Assert.Equal(32, last.OpenFindings);     // both count open findings
    }

    [Fact]
    public void Weeks_are_clamped()
    {
        var runs = new List<CompletedRunPoint> { Run(A, "2020-01-01", 50, 1) };
        Assert.Equal(PortfolioTrend.MaxWeeks, PortfolioTrend.Compute(runs, new DateOnly(2026, 8, 29), weeks: 9999).Count);
        Assert.Equal(2, PortfolioTrend.Compute(runs, new DateOnly(2026, 8, 29), weeks: 0).Count);
    }
}
