using Atlas.Domain.Findings;

namespace Atlas.Domain.Tests;

public class SuppressionExpiryTests
{
    [Fact]
    public void Policies_stop_matching_after_their_expiry()
    {
        var policy = new SuppressionPolicy(Guid.NewGuid(), Guid.NewGuid(), null, "quality.*", null, "seasonal noise", "ana",
            DateTimeOffset.UtcNow.AddDays(30));

        Assert.True(policy.IsActive(DateTimeOffset.UtcNow));
        Assert.False(policy.IsActive(DateTimeOffset.UtcNow.AddDays(31)));

        var openEnded = new SuppressionPolicy(Guid.NewGuid(), Guid.NewGuid(), null, "quality.*", null, "noise", "ana");
        Assert.True(openEnded.IsActive(DateTimeOffset.UtcNow.AddYears(10)));
    }

    [Fact]
    public void Past_expiry_is_rejected_on_creation()
    {
        Assert.Throws<ArgumentException>(() =>
            new SuppressionPolicy(Guid.NewGuid(), Guid.NewGuid(), null, "quality.*", null, "why", "ana", DateTimeOffset.UtcNow.AddMinutes(-1)));
    }
}
