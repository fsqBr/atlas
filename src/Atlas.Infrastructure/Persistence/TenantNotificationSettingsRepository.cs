using Atlas.Application.Assessments;
using Atlas.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure.Persistence;

public sealed class TenantNotificationSettingsRepository(AtlasDbContext db) : ITenantNotificationSettingsRepository
{
    public Task<TenantNotificationSettings?> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        // Explicit tenant filter: the worker sends notifications without an ambient tenant.
        db.TenantNotificationSettings.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

    public async Task<IReadOnlyList<TenantNotificationSettings>> ListAllAsync(CancellationToken cancellationToken) =>
        await db.TenantNotificationSettings.IgnoreQueryFilters().ToListAsync(cancellationToken);

    public void Add(TenantNotificationSettings settings) => db.TenantNotificationSettings.Add(settings);

    public void Remove(TenantNotificationSettings settings) => db.TenantNotificationSettings.Remove(settings);
}
