using Atlas.Domain.Assessments;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;

namespace Atlas.Domain.Tests;

public class TargetsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, null, null, TargetStatus.None)]
    [InlineData(72, 70, null, TargetStatus.Met)]
    [InlineData(60, 70, null, TargetStatus.OnTrack)]
    [InlineData(40, 70, null, TargetStatus.AtRisk)] // more than 20 points away
    [InlineData(60, 70, "2026-09-10", TargetStatus.AtRisk)] // due within 30 days
    [InlineData(60, 70, "2026-12-31", TargetStatus.OnTrack)]
    [InlineData(60, 70, "2026-08-01", TargetStatus.Missed)]
    [InlineData(null, 70, "2026-12-31", TargetStatus.OnTrack)] // not scored yet
    public void Evaluates_target_status(int? score, int? target, string? due, TargetStatus expected)
    {
        var date = due is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(due + "T00:00:00Z");
        Assert.Equal(expected, Targets.Evaluate(score, target, date, Now));
    }

    [Fact]
    public void Assessment_validates_targets()
    {
        var a = new Assessment(Guid.NewGuid(), WellKnownTenants.DefaultId, "A", new SourceReference("local", "/x"));
        a.SetTarget(70, Now.AddMonths(3));
        Assert.Equal(70, a.TargetScore);
        Assert.Equal(TargetStatus.OnTrack, a.TargetStatusAt(55, Now));
        Assert.Equal(TargetStatus.Met, a.TargetStatusAt(70, Now));

        Assert.Throws<ArgumentException>(() => a.SetTarget(0, null));
        Assert.Throws<ArgumentException>(() => a.SetTarget(101, null));
        Assert.Throws<ArgumentException>(() => a.SetTarget(null, Now));

        a.SetTarget(null, null);
        Assert.Equal(TargetStatus.None, a.TargetStatusAt(10, Now));
    }
}
