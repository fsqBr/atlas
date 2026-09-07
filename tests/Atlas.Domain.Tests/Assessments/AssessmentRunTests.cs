using Atlas.Domain.Assessments;

namespace Atlas.Domain.Tests.Assessments;

public class AssessmentRunTests
{
    private static AssessmentRun NewRun(int number = 1) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), number);

    [Fact]
    public void Starts_running_and_numbered()
    {
        var run = NewRun(3);

        Assert.Equal(3, run.Number);
        Assert.Equal(AssessmentRunStatus.Running, run.Status);
        Assert.Null(run.FinishedAtUtc);
        Assert.Throws<ArgumentException>(() => NewRun(0));
    }

    [Fact]
    public void Accumulates_scan_totals_and_completes_clean()
    {
        var run = NewRun();
        run.RecordScan(true, created: 5, recurring: 2, resolved: 1, regressed: 0);
        run.RecordScan(true, created: 1, recurring: 0, resolved: 3, regressed: 1);

        run.Complete(openFindings: 42, healthScore: 71);

        Assert.Equal(2, run.ScannersRun);
        Assert.Equal(0, run.ScannersFailed);
        Assert.Equal(6, run.FindingsNew);
        Assert.Equal(4, run.FindingsResolved);
        Assert.Equal(1, run.FindingsRegressed);
        Assert.Equal(42, run.OpenFindings);
        Assert.Equal(71, run.HealthScore);
        Assert.Equal(AssessmentRunStatus.Completed, run.Status);
        Assert.NotNull(run.FinishedAtUtc);
    }

    [Fact]
    public void A_failed_scanner_degrades_to_completed_with_warnings()
    {
        var run = NewRun();
        run.RecordScan(true, 1, 0, 0, 0);
        run.RecordScan(false, 0, 0, 0, 0);

        run.Complete(1, 90);

        Assert.Equal(1, run.ScannersFailed);
        Assert.Equal(AssessmentRunStatus.CompletedWithWarnings, run.Status);
    }

    [Fact]
    public void Fail_records_reason_and_blocks_complete()
    {
        var run = NewRun();
        run.Fail("clone failed");

        Assert.Equal(AssessmentRunStatus.Failed, run.Status);
        Assert.Equal("clone failed", run.FailureReason);
        Assert.Throws<InvalidOperationException>(() => run.Complete(0, 100));
    }
}
