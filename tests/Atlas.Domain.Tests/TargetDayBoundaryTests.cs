using Atlas.Domain.Assessments;

namespace Atlas.Domain.Tests;

/// <summary>Regression for the 2026-08 rule audit: a goal is not "Missed" on the morning of its own due date.</summary>
public class TargetDayBoundaryTests
{
    private static readonly DateTimeOffset Due = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_whole_target_day_counts_before_missed()
    {
        // 09:00 UTC on the due date: still evaluable, not Missed.
        Assert.Equal(TargetStatus.AtRisk, Targets.Evaluate(40, 70, Due, new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero)));

        // 23:59 UTC on the due date: still not Missed.
        Assert.NotEqual(TargetStatus.Missed, Targets.Evaluate(40, 70, Due, new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero)));

        // First instant of the next UTC day: Missed.
        Assert.Equal(TargetStatus.Missed, Targets.Evaluate(40, 70, Due, new DateTimeOffset(2026, 10, 1, 0, 0, 1, TimeSpan.Zero)));

        // Meeting the score wins regardless of the date.
        Assert.Equal(TargetStatus.Met, Targets.Evaluate(70, 70, Due, new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)));
    }
}
