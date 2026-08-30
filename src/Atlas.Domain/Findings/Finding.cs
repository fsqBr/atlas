namespace Atlas.Domain.Findings;

/// <summary>
/// Logical finding: one per stable fingerprint within an assessment.
/// Lifecycle is driven by reconciliation, never by scanners directly. Per-scan
/// details (location, message, confidence) live in FindingOccurrence.
/// </summary>
public sealed class Finding
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public string Fingerprint { get; private set; } = null!;
    public string RuleId { get; private set; } = null!;
    public FindingCategory Category { get; private set; }
    public Severity Severity { get; private set; }
    public string Title { get; private set; } = null!;
    public FindingStatus Status { get; private set; }
    public FindingOrigin Origin { get; private set; }
    public Guid FirstSeenScanId { get; private set; }
    public Guid LastSeenScanId { get; private set; }
    public Guid? ResolvedScanId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private Finding()
    {
    }

    public static Finding Create(
        Guid id,
        Guid tenantId,
        Guid assessmentId,
        string fingerprint,
        string ruleId,
        FindingCategory category,
        Severity severity,
        string title,
        FindingOrigin origin,
        Guid scanId)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("Fingerprint must not be empty.", nameof(fingerprint));
        }

        var now = DateTimeOffset.UtcNow;
        return new Finding
        {
            Id = id,
            TenantId = tenantId,
            AssessmentId = assessmentId,
            Fingerprint = fingerprint,
            RuleId = ruleId,
            Category = category,
            Severity = severity,
            Title = title,
            Status = FindingStatus.Open,
            Origin = origin,
            FirstSeenScanId = scanId,
            LastSeenScanId = scanId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    /// <summary>Seen again in a scan: recurring if open, regressed if it had been resolved. Suppressions are sticky.</summary>
    public void Seen(Guid scanId, Severity severity, string title)
    {
        LastSeenScanId = scanId;
        Severity = severity;
        Title = title;

        if (Status == FindingStatus.Resolved)
        {
            Status = FindingStatus.Regressed;
            ResolvedScanId = null;
        }

        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Not seen by a successful scan of its own rule: resolved. Suppressed/false-positive findings stay as they are.</summary>
    public bool TryResolve(Guid scanId)
    {
        if (Status is not (FindingStatus.Open or FindingStatus.Regressed))
        {
            return false;
        }

        Status = FindingStatus.Resolved;
        ResolvedScanId = scanId;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public void Suppress()
    {
        Status = FindingStatus.Suppressed;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void MarkFalsePositive()
    {
        Status = FindingStatus.FalsePositive;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Undo a triage decision; the finding is open again and reconciliation resumes deciding its fate.</summary>
    public void Reopen()
    {
        if (Status is not (FindingStatus.Suppressed or FindingStatus.FalsePositive))
        {
            throw new InvalidOperationException($"Finding {Id} is {Status}; only suppressed or false-positive findings can be reopened.");
        }

        Status = FindingStatus.Open;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
