namespace Atlas.Domain.Findings;

/// <summary>
/// Auditable triage decision on a finding: who decided, why, and when.
/// The decision is sticky across scans because it attaches to the finding's
/// stable fingerprint; reopening revokes it instead of deleting history.
/// </summary>
public sealed class FindingSuppression
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public Guid FindingId { get; private set; }
    public string Fingerprint { get; private set; } = null!;
    public SuppressionKind Kind { get; private set; }
    public string Reason { get; private set; } = null!;
    public string Author { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Optional waiver end ("accepted for 90 days"): a sweep reopens the finding after this instant.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public string? RevokedBy { get; private set; }

    private FindingSuppression()
    {
    }

    public FindingSuppression(Guid id, Finding finding, SuppressionKind kind, string reason, string author, DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required for an auditable suppression.", nameof(reason));
        }

        if (string.IsNullOrWhiteSpace(author))
        {
            throw new ArgumentException("An author is required for an auditable suppression.", nameof(author));
        }

        Id = id;
        TenantId = finding.TenantId;
        AssessmentId = finding.AssessmentId;
        FindingId = finding.Id;
        Fingerprint = finding.Fingerprint;
        if (expiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Expiry must be in the future.", nameof(expiresAtUtc));
        }

        Kind = kind;
        ExpiresAtUtc = expiresAtUtc;
        Reason = reason.Trim();
        Author = author.Trim();
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public bool IsActive => RevokedAtUtc is null;

    public void Revoke(string by)
    {
        if (!IsActive)
        {
            return;
        }

        RevokedAtUtc = DateTimeOffset.UtcNow;
        RevokedBy = string.IsNullOrWhiteSpace(by) ? "unknown" : by.Trim();
    }
}

public enum SuppressionKind
{
    /// <summary>Real, but accepted for now (risk acknowledged, out of scope, planned).</summary>
    Suppressed,

    /// <summary>Not real: the rule misfired here. Feeds the false-positive rate.</summary>
    FalsePositive,
}
