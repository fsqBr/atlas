using Atlas.Domain.Jobs;

namespace Atlas.Domain.Tests.Jobs;

public class ScanJobTests
{
    private static ScanJob NewJob() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Queued_job_is_claimable_and_start_requires_lease()
    {
        var job = NewJob();
        Assert.True(job.IsClaimable(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(job.Start);

        job.Claim("w1", TimeSpan.FromMinutes(5));

        Assert.Equal(ScanJobState.Leased, job.State);
        Assert.Equal(1, job.Attempt);
        Assert.False(job.IsClaimable(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Expired_lease_becomes_claimable_again()
    {
        var job = NewJob();
        job.Claim("w1", TimeSpan.FromMilliseconds(1));
        job.Start();

        Assert.True(job.IsClaimable(DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public void Failure_requeues_until_attempts_exhausted_then_dead_letters()
    {
        var job = NewJob();

        for (var attempt = 1; attempt < ScanJob.MaxAttempts; attempt++)
        {
            job.Claim("w", TimeSpan.FromMinutes(1));
            job.Start();
            job.Fail($"boom {attempt}");
            Assert.Equal(ScanJobState.Queued, job.State);
            Assert.Equal($"boom {attempt}", job.Error);
        }

        job.Claim("w", TimeSpan.FromMinutes(1));
        job.Start();
        job.Fail("final");

        Assert.Equal(ScanJobState.DeadLetter, job.State);
        Assert.Equal(ScanJob.MaxAttempts, job.Attempt);
        Assert.Null(job.LeasedBy);
    }

    [Fact]
    public void Success_clears_lease()
    {
        var job = NewJob();
        job.Claim("w", TimeSpan.FromMinutes(1));
        job.Start();
        job.Succeed();

        Assert.Equal(ScanJobState.Succeeded, job.State);
        Assert.Null(job.LeaseExpiresAtUtc);
        Assert.NotNull(job.FinishedAtUtc);
    }
}
