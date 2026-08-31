namespace Atlas.Domain.Assessments;

public enum AccessRole
{
    Viewer = 0,
    Editor = 1,
    Owner = 2,
}

/// <summary>
/// Per-assessment access inside a tenant. An assessment with no entries is open
/// to everyone in the tenant (the default); the first entry restricts it to the
/// listed subjects — plus tenant admins, who always see everything. Subjects are
/// identity-token subjects (or e-mails) and service-token ids.
/// </summary>
public sealed class AssessmentAccess
{
    public const int MaxSubjectLength = 200;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public string Subject { get; private set; } = null!;
    public string? SubjectName { get; private set; }
    public AccessRole Role { get; private set; }
    public string GrantedBy { get; private set; } = null!;
    public DateTimeOffset GrantedAtUtc { get; private set; }

    private AssessmentAccess()
    {
    }

    public AssessmentAccess(Guid id, Guid tenantId, Guid assessmentId, string subject, string? subjectName, AccessRole role, string grantedBy)
    {
        Id = id;
        TenantId = tenantId;
        AssessmentId = assessmentId;
        Subject = NormalizeSubject(subject);
        SubjectName = string.IsNullOrWhiteSpace(subjectName) ? null : subjectName.Trim();
        Role = role;
        GrantedBy = string.IsNullOrWhiteSpace(grantedBy) ? "anonymous" : grantedBy.Trim();
        GrantedAtUtc = DateTimeOffset.UtcNow;
    }

    public void SetRole(AccessRole role) => Role = role;

    public static string NormalizeSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("Subject is required (user id, e-mail or token id).", nameof(subject));
        }

        var normalized = subject.Trim();
        if (normalized.Length > MaxSubjectLength)
        {
            throw new ArgumentException($"Subject must be at most {MaxSubjectLength} characters.", nameof(subject));
        }

        return normalized.Contains('@') ? normalized.ToLowerInvariant() : normalized;
    }

    public static bool CanEdit(AccessRole role) => role is AccessRole.Editor or AccessRole.Owner;
}
