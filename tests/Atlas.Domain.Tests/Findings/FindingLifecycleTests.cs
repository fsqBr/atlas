using Atlas.Domain.Findings;

namespace Atlas.Domain.Tests.Findings;

public class FindingLifecycleTests
{
    private static Finding NewFinding(Guid scanId) => Finding.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "fp", "rule", FindingCategory.Dependencies,
        Severity.High, "title", FindingOrigin.Deterministic, scanId);

    [Fact]
    public void New_finding_is_open_and_seen_in_creating_scan()
    {
        var scan = Guid.NewGuid();
        var finding = NewFinding(scan);

        Assert.Equal(FindingStatus.Open, finding.Status);
        Assert.Equal(scan, finding.FirstSeenScanId);
        Assert.Equal(scan, finding.LastSeenScanId);
    }

    [Fact]
    public void Seen_again_updates_last_scan_and_severity()
    {
        var finding = NewFinding(Guid.NewGuid());
        var later = Guid.NewGuid();

        finding.Seen(later, Severity.Critical, "worse");

        Assert.Equal(FindingStatus.Open, finding.Status);
        Assert.Equal(later, finding.LastSeenScanId);
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Equal("worse", finding.Title);
    }

    [Fact]
    public void Resolve_then_seen_means_regressed()
    {
        var finding = NewFinding(Guid.NewGuid());
        var resolvingScan = Guid.NewGuid();

        Assert.True(finding.TryResolve(resolvingScan));
        Assert.Equal(FindingStatus.Resolved, finding.Status);
        Assert.Equal(resolvingScan, finding.ResolvedScanId);

        finding.Seen(Guid.NewGuid(), Severity.High, "title");

        Assert.Equal(FindingStatus.Regressed, finding.Status);
        Assert.Null(finding.ResolvedScanId);
    }

    [Fact]
    public void Suppressed_and_false_positive_are_sticky()
    {
        var suppressed = NewFinding(Guid.NewGuid());
        suppressed.Suppress();
        suppressed.Seen(Guid.NewGuid(), Severity.High, "t");
        Assert.Equal(FindingStatus.Suppressed, suppressed.Status);
        Assert.False(suppressed.TryResolve(Guid.NewGuid()));

        var falsePositive = NewFinding(Guid.NewGuid());
        falsePositive.MarkFalsePositive();
        Assert.False(falsePositive.TryResolve(Guid.NewGuid()));
        Assert.Equal(FindingStatus.FalsePositive, falsePositive.Status);
    }
}
