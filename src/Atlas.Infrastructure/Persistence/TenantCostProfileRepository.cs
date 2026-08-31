using Atlas.Application.Assessments;
using Atlas.Domain.Modernization;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure.Persistence;

public sealed class TenantCostProfileRepository(AtlasDbContext db) : ITenantCostProfileRepository
{
    public Task<TenantCostProfile?> GetAsync(CancellationToken cancellationToken) =>
        db.TenantCostProfiles.SingleOrDefaultAsync(cancellationToken);

    public Task<TenantCostProfile?> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        // Explicit tenant filter: the worker computes plans (AI features) without a tenant context.
        db.TenantCostProfiles.IgnoreQueryFilters().SingleOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);

    public void Add(TenantCostProfile profile) => db.TenantCostProfiles.Add(profile);

    public void Remove(TenantCostProfile profile) => db.TenantCostProfiles.Remove(profile);
}
