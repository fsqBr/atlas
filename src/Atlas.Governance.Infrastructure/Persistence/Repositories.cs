using Atlas.Governance.Application;
using Atlas.Governance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Governance.Infrastructure.Persistence;

public sealed class EfGovernanceUnitOfWork(GovernanceDbContext db) : IGovernanceUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

public sealed class CostSourceRepository(GovernanceDbContext db) : ICostSourceRepository
{
    public async Task<IReadOnlyList<CostSource>> ListAsync(CancellationToken cancellationToken) =>
        await db.CostSources.OrderBy(s => s.Provider).ToListAsync(cancellationToken);

    public Task<CostSource?> GetAsync(string provider, CancellationToken cancellationToken) =>
        db.CostSources.SingleOrDefaultAsync(s => s.Provider == provider, cancellationToken);

    public void Add(CostSource source) => db.CostSources.Add(source);

    public void Remove(CostSource source) => db.CostSources.Remove(source);
}

public sealed class CostFactRepository(GovernanceDbContext db) : ICostFactRepository
{
    public async Task ReplaceWindowAsync(Guid sourceId, DateOnly from, DateOnly to, IReadOnlyList<CostFact> facts, CancellationToken cancellationToken)
    {
        // Delete-then-insert inside the caller's SaveChanges: a retried sync converges to the same rows.
        var stale = await db.CostFacts.Where(f => f.SourceId == sourceId && f.Period >= from && f.Period <= to).ToListAsync(cancellationToken);
        db.CostFacts.RemoveRange(stale);
        db.CostFacts.AddRange(facts.Where(f => f.Period >= from && f.Period <= to));
    }

    public async Task<IReadOnlyList<CostFact>> ListAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.CostFacts.Where(f => f.Period >= from && f.Period <= to).OrderBy(f => f.Period).ThenBy(f => f.Provider).ToListAsync(cancellationToken);
}

public sealed class SeatFactRepository(GovernanceDbContext db) : ISeatFactRepository
{
    public async Task ReplaceAsync(Guid sourceId, IReadOnlyList<SeatFact> seats, CancellationToken cancellationToken)
    {
        var stale = await db.SeatFacts.Where(s => s.SourceId == sourceId).ToListAsync(cancellationToken);
        db.SeatFacts.RemoveRange(stale);
        db.SeatFacts.AddRange(seats.GroupBy(s => s.SeatKey, StringComparer.Ordinal).Select(g => g.First()));
    }

    public async Task<IReadOnlyList<SeatFact>> ListAsync(CancellationToken cancellationToken) =>
        await db.SeatFacts.OrderBy(s => s.Provider).ToListAsync(cancellationToken);
}

public sealed class UsageFactRepository(GovernanceDbContext db) : IUsageFactRepository
{
    public async Task UpsertAsync(IReadOnlyList<UsageFact> facts, CancellationToken cancellationToken)
    {
        if (facts.Count == 0)
        {
            return;
        }

        var tenantId = facts[0].TenantId;
        var actor = facts[0].Actor;
        var tool = facts[0].Tool;
        var periods = facts.Select(f => f.Period).Distinct().ToList();
        var existing = await db.UsageFacts
            .Where(u => u.TenantId == tenantId && u.Actor == actor && u.Tool == tool && periods.Contains(u.Period))
            .ToListAsync(cancellationToken);
        var stale = existing.Where(e => facts.Any(f => f.Period == e.Period && string.Equals(f.Model, e.Model, StringComparison.OrdinalIgnoreCase))).ToList();
        db.UsageFacts.RemoveRange(stale);
        db.UsageFacts.AddRange(facts);
    }

    public async Task<IReadOnlyList<UsageFact>> ListAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await db.UsageFacts.Where(u => u.Period >= from && u.Period <= to).OrderBy(u => u.Period).ThenBy(u => u.Actor).ToListAsync(cancellationToken);
}

public sealed class ModelPriceRepository(GovernanceDbContext db) : IModelPriceRepository
{
    public async Task<IReadOnlyList<ModelPriceOverride>> ListAsync(CancellationToken cancellationToken) =>
        await db.ModelPrices.OrderBy(p => p.Pattern).ToListAsync(cancellationToken);

    public void Add(ModelPriceOverride price) => db.ModelPrices.Add(price);

    public void Remove(ModelPriceOverride price) => db.ModelPrices.Remove(price);
}

public sealed class UsageEventRepository(GovernanceDbContext db) : IUsageEventRepository
{
    public void Add(UsageReportEvent evt) => db.UsageReportEvents.Add(evt);

    public async Task<IReadOnlyList<UsageReportEvent>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        await db.UsageReportEvents.Where(e => e.ReportedAtUtc >= sinceUtc).OrderBy(e => e.ReportedAtUtc).ToListAsync(cancellationToken);

    public Task PruneBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) =>
        db.UsageReportEvents.Where(e => e.ReportedAtUtc < cutoffUtc).ExecuteDeleteAsync(cancellationToken);
}
