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
    public void Empty_estate_yields_no_digest()
    {
        Assert.Null(PortfolioDigestBuilder.Build([], [], Now));
    }
}
