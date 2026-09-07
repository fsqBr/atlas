using Atlas.Application.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure.Persistence;

public sealed class RuleOverrideRepository(AtlasDbContext db) : IRuleOverrideRepository
{
    public async Task<IReadOnlyList<RuleSeverityOverride>> ListAsync(CancellationToken cancellationToken) =>
        await db.RuleSeverityOverrides.OrderBy(o => o.RuleId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, Severity>> MapForTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        // Explicit tenant filter: the worker runs without a tenant context (query filter disabled).
        await db.RuleSeverityOverrides.IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId)
            .ToDictionaryAsync(o => o.RuleId, o => o.Severity, cancellationToken);

    public Task<RuleSeverityOverride?> GetAsync(string ruleId, CancellationToken cancellationToken) =>
        db.RuleSeverityOverrides.SingleOrDefaultAsync(o => o.RuleId == ruleId, cancellationToken);

    public void Add(RuleSeverityOverride @override) => db.RuleSeverityOverrides.Add(@override);

    public void Remove(RuleSeverityOverride @override) => db.RuleSeverityOverrides.Remove(@override);
}
