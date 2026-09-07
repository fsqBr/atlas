using Atlas.Domain.Findings;

namespace Atlas.Domain.Tests.Findings;

public class FindingSuppressionTests
{
    private static Finding NewFinding() => Finding.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "fp-1", "rule", FindingCategory.Security,
        Severity.High, "title", FindingOrigin.Deterministic, Guid.NewGuid());

    [Fact]
    public void Suppression_records_who_why_and_binds_to_the_fingerprint()
    {
        var finding = NewFinding();
        var suppression = new FindingSuppression(Guid.NewGuid(), finding, SuppressionKind.FalsePositive, " test fixture ", " Ana ");

        Assert.Equal(finding.Fingerprint, suppression.Fingerprint);
        Assert.Equal(finding.AssessmentId, suppression.AssessmentId);
        Assert.Equal("test fixture", suppression.Reason);
        Assert.Equal("Ana", suppression.Author);
        Assert.True(suppression.IsActive);
    }

    [Fact]
    public void Reason_and_author_are_mandatory()
    {
        var finding = NewFinding();

        Assert.Throws<ArgumentException>(() => new FindingSuppression(Guid.NewGuid(), finding, SuppressionKind.Suppressed, "", "Ana"));
        Assert.Throws<ArgumentException>(() => new FindingSuppression(Guid.NewGuid(), finding, SuppressionKind.Suppressed, "why", " "));
    }

    [Fact]
    public void Revoke_keeps_history_and_is_idempotent()
    {
        var suppression = new FindingSuppression(Guid.NewGuid(), NewFinding(), SuppressionKind.Suppressed, "accepted", "Ana");

        suppression.Revoke("Bruno");
        var first = suppression.RevokedAtUtc;
        suppression.Revoke("Carla");

        Assert.False(suppression.IsActive);
        Assert.Equal("Bruno", suppression.RevokedBy);
        Assert.Equal(first, suppression.RevokedAtUtc);
    }

    [Fact]
    public void Reopen_only_from_triaged_states()
    {
        var finding = NewFinding();
        Assert.Throws<InvalidOperationException>(finding.Reopen);

        finding.Suppress();
        finding.Reopen();
        Assert.Equal(FindingStatus.Open, finding.Status);

        finding.MarkFalsePositive();
        finding.Reopen();
        Assert.Equal(FindingStatus.Open, finding.Status);
    }
}
