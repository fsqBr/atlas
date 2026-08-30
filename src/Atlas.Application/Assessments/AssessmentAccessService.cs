using Atlas.Application.Tenants;
using Atlas.Domain.Assessments;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Assessments;

public interface IAssessmentAccessRepository
{
    Task<IReadOnlyList<AssessmentAccess>> ListAsync(Guid assessmentId, CancellationToken cancellationToken);

    void Add(AssessmentAccess access);

    void Remove(AssessmentAccess access);
}

public sealed record AccessEntry(Guid Id, string Subject, string? SubjectName, AccessRole Role, string GrantedBy, DateTimeOffset GrantedAtUtc);

public sealed record AccessView(bool Restricted, AccessRole? MyRole, bool CanManage, bool CanEdit, IReadOnlyList<AccessEntry> Entries);

/// <summary>
/// Sharing inside a tenant: who can see/edit/manage an assessment. Visibility of
/// restricted assessments is enforced by the DbContext query filter (hidden rows
/// are 404s); this service answers "what may the current subject do" and manages
/// the list. Tenant admins and system scope can do everything.
/// </summary>
public sealed class AssessmentAccessService(
    IAssessmentAccessRepository repository,
    IAssessmentRepository assessments,
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ILogger<AssessmentAccessService> logger)
{
    public async Task<AccessView> GetAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var entries = await repository.ListAsync(assessmentId, cancellationToken);
        var mine = tenant.Subject is null ? null : entries.FirstOrDefault(e => e.Subject.Equals(AssessmentAccess.NormalizeSubject(tenant.Subject), StringComparison.Ordinal))?.Role;
        var restricted = entries.Count > 0;
        var canManage = tenant.IsAdmin || mine == AccessRole.Owner;
        var canEdit = tenant.IsAdmin || !restricted || (mine is { } r && AssessmentAccess.CanEdit(r));
        return new AccessView(restricted, mine, canManage, canEdit,
            entries.OrderBy(e => e.Role == AccessRole.Owner ? 0 : e.Role == AccessRole.Editor ? 1 : 2).ThenBy(e => e.Subject, StringComparer.OrdinalIgnoreCase)
                .Select(e => new AccessEntry(e.Id, e.Subject, e.SubjectName, e.Role, e.GrantedBy, e.GrantedAtUtc)).ToList());
    }

    /// <summary>May the current subject change this assessment (run, triage, schedule, scope…)?</summary>
    public async Task<bool> CanEditAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        (await GetAsync(assessmentId, cancellationToken)).CanEdit;

    public async Task<AccessView> GrantAsync(Guid assessmentId, string subject, string? subjectName, AccessRole role, CancellationToken cancellationToken)
    {
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken) ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        await EnsureCanManageAsync(assessmentId, cancellationToken);

        var normalized = AssessmentAccess.NormalizeSubject(subject);
        var entries = await repository.ListAsync(assessmentId, cancellationToken);
        var existing = entries.FirstOrDefault(e => e.Subject.Equals(normalized, StringComparison.Ordinal));
        if (existing is null)
        {
            // The first restriction must keep the person doing it in control: they become owner unless admin.
            if (entries.Count == 0 && !tenant.IsAdmin && tenant.Subject is not null && !AssessmentAccess.NormalizeSubject(tenant.Subject).Equals(normalized, StringComparison.Ordinal))
            {
                repository.Add(new AssessmentAccess(Guid.NewGuid(), assessment.TenantId, assessmentId, tenant.Subject, tenant.SubjectName, AccessRole.Owner, tenant.Subject));
            }

            repository.Add(new AssessmentAccess(Guid.NewGuid(), assessment.TenantId, assessmentId, normalized, subjectName, role, tenant.Subject ?? "anonymous"));
        }
        else
        {
            existing.SetRole(role);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Access to assessment {AssessmentId}: {Subject} → {Role} by {Actor}.", assessmentId, normalized, role, tenant.Subject ?? "anonymous");
        return await GetAsync(assessmentId, cancellationToken);
    }

    public async Task<AccessView> RevokeAsync(Guid assessmentId, Guid entryId, CancellationToken cancellationToken)
    {
        _ = await assessments.GetAsync(assessmentId, cancellationToken) ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        await EnsureCanManageAsync(assessmentId, cancellationToken);

        var entries = await repository.ListAsync(assessmentId, cancellationToken);
        var entry = entries.FirstOrDefault(e => e.Id == entryId) ?? throw new KeyNotFoundException($"Access entry {entryId} not found.");
        if (entry.Role == AccessRole.Owner && entries.Count(e => e.Role == AccessRole.Owner) == 1 && entries.Count > 1 && !tenant.IsAdmin)
        {
            throw new InvalidOperationException("Cannot remove the last owner while other entries exist; make someone else owner first or open the assessment.");
        }

        repository.Remove(entry);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Access to assessment {AssessmentId}: {Subject} removed by {Actor}.", assessmentId, entry.Subject, tenant.Subject ?? "anonymous");
        return await GetAsync(assessmentId, cancellationToken);
    }

    private async Task EnsureCanManageAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var view = await GetAsync(assessmentId, cancellationToken);
        if (!view.CanManage && view.Restricted)
        {
            throw new UnauthorizedAccessException("Only owners and tenant administrators can change who has access.");
        }
    }
}
