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
