using Atlas.Domain.Findings;

namespace Atlas.Domain.Rules;

/// <summary>
/// A tenant's decision that a rule weighs differently in THEIR estate than the catalog default
/// (e.g. "SELECT * is Low for us, not Medium"). Applied to candidates at scan time — findings
/// created from the next run on carry the tenant severity, and the fingerprint (which excludes
/// severity) keeps their history intact. Suppressing a rule entirely stays the job of
/// suppression policies, which carry reason and audit trail.
/// </summary>
public sealed class RuleSeverityOverride
{
    public Guid TenantId { get; private set; }
    public string RuleId { get; private set; } = null!;
    public Severity Severity { get; private set; }
    public string UpdatedBy { get; private set; } = null!;
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private RuleSeverityOverride()
    {
    }

    public RuleSeverityOverride(Guid tenantId, string ruleId, Severity severity, string updatedBy)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException("Rule id must not be empty.", nameof(ruleId));
        }

        TenantId = tenantId;
        RuleId = ruleId;
        Update(severity, updatedBy);
    }

    public void Update(Severity severity, string updatedBy)
    {
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentException("Unknown severity.", nameof(severity));
        }

        Severity = severity;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
