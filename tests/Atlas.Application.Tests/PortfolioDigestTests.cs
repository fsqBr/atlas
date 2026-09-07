using Atlas.Application.Portfolio;

namespace Atlas.Application.Tests;

public class PortfolioDigestTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-30T13:00:00Z");

    private static CompletedRunPoint At(Guid id, int daysAgo, int score, int open) =>
        new(id, Now.AddDays(-daysAgo), score, open);

    [Fact]
    public void Digest_compares_now_with_seven_days_ago_and_ranks_movers()
    {
        var points = new List<CompletedRunPoint>
        {
            At(A, 10, 50, 100), At(A, 1, 62, 80),   // A improved 12
            At(B, 9, 90, 10), At(B, 2, 86, 14),     // B dropped 4
        };
        var assessments = new List<DigestAssessment>
        {
            new(A, "Alpha", 70, Now.AddDays(10)),   // 62 < 70, due in 10 days → AtRisk
            new(B, "Beta", null, null),
        };

        var digest = PortfolioDigestBuilder.Build(points, assessments, Now)!;

        Assert.Equal(74.0, digest.AverageScore);            // (62+86)/2
        Assert.Equal(4, digest.AverageDelta);               // 74 - 70
        Assert.Equal(94, digest.OpenFindings);
        Assert.Equal(-16, digest.OpenDelta);                // 94 - 110
        Assert.Equal(2, digest.Assessed);
        Assert.Equal("Alpha", digest.Movers[0].Name);       // |12| > |4|
        Assert.Equal(50, digest.Movers[0].From);
        Assert.Equal(62, digest.Movers[0].To);
        Assert.Equal(1, digest.TargetsAtRisk);
        Assert.Equal(0, digest.TargetsMissed);
    }

    [Fact]
    public void A_newly_onboarded_assessment_does_not_swing_the_week_over_week_deltas()
    {
        // A exists at both endpoints (improved 12, −20 open). B was first scanned THIS week (present
        // now, absent a week ago) with 40 open findings — it must not read as "the estate got worse".
        var points = new List<CompletedRunPoint>
        {
            At(A, 9, 50, 100), At(A, 1, 62, 80),
            At(B, 1, 55, 40),
        };
        var assessments = new List<DigestAssessment> { new(A, "Alpha", null, null), new(B, "Beta", null, null) };

        var digest = PortfolioDigestBuilder.Build(points, assessments, Now)!;

        Assert.Equal(58.5, digest.AverageScore);   // current average over A and B (62+55)/2
        Assert.Equal(12, digest.AverageDelta);      // paired on A only: 62 − 50
        Assert.Equal(120, digest.OpenFindings);     // 80 + 40
        Assert.Equal(-20, digest.OpenDelta);        // paired on A only: 80 − 100, NOT 120 − 100
        Assert.Single(digest.Movers);               // only A has both endpoints
    }

    [Fact]
    public void Empty_estate_yields_no_digest()
    {
        Assert.Null(PortfolioDigestBuilder.Build([], [], Now));
    }
}
