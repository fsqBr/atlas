using Atlas.Domain.Audit;

namespace Atlas.Application.Audit;

public interface IAuditRepository
{
    void Add(AuditEntry entry);

    Task<IReadOnlyList<AuditEntry>> ListRecentAsync(int take, Guid? assessmentId, CancellationToken cancellationToken);
}
