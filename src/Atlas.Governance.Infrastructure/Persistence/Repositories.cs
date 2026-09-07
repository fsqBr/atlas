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

    public Task<UsageFact?> GetAsync(string actor, string tool, DateOnly period, string model, CancellationToken cancellationToken) =>
        db.UsageFacts.FirstOrDefaultAsync(u => u.Actor == actor && u.Tool == tool && u.Period == period && u.Model == model, cancellationToken);

    public void Add(UsageFact fact) => db.UsageFacts.Add(fact);

    public async Task<IReadOnlyList<UsageFact>> ListForActorAsync(string actor, string tool, DateOnly period, CancellationToken cancellationToken) =>
        await db.UsageFacts.Where(u => u.Actor == actor && u.Tool == tool && u.Period == period).ToListAsync(cancellationToken);

    public async Task ReplaceToolWindowAsync(string tool, DateOnly from, DateOnly to, IReadOnlyList<UsageFact> facts, CancellationToken cancellationToken)
    {
        var stale = await db.UsageFacts.Where(u => u.Tool == tool && u.Period >= from && u.Period <= to).ToListAsync(cancellationToken);
        db.UsageFacts.RemoveRange(stale);
        db.UsageFacts.AddRange(facts);
    }
}

public sealed class TelemetryStreamRepository(GovernanceDbContext db) : ITelemetryStreamRepository
{
    public async Task<IReadOnlyList<TelemetryStream>> GetAsync(IReadOnlyList<string> streamKeys, CancellationToken cancellationToken) =>
        await db.TelemetryStreams.Where(s => streamKeys.Contains(s.StreamKey)).ToListAsync(cancellationToken);

    public void Add(TelemetryStream stream) => db.TelemetryStreams.Add(stream);

    public Task PruneBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) =>
        db.TelemetryStreams.Where(s => s.UpdatedAtUtc < cutoffUtc).ExecuteDeleteAsync(cancellationToken);
}

public sealed class BudgetRepository(GovernanceDbContext db) : IBudgetRepository
{
    public async Task<IReadOnlyList<Budget>> ListAsync(CancellationToken cancellationToken) =>
        await db.Budgets.OrderBy(b => b.Scope).ThenBy(b => b.ScopeKey).ToListAsync(cancellationToken);

    public void Add(Budget budget) => db.Budgets.Add(budget);

    public void Remove(Budget budget) => db.Budgets.Remove(budget);

    public void AddAlert(BudgetAlert alert) => db.BudgetAlerts.Add(alert);

    public async Task<IReadOnlyList<BudgetAlert>> ListAlertsAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        await db.BudgetAlerts.Where(a => a.CreatedAtUtc >= sinceUtc).OrderByDescending(a => a.CreatedAtUtc).Take(200).ToListAsync(cancellationToken);

    public async Task<HashSet<string>> AlertKeysAsync(DateOnly since, CancellationToken cancellationToken)
    {
        var cutoff = new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var keys = await db.BudgetAlerts.Where(a => a.CreatedAtUtc >= cutoff).Select(a => a.Key).ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }
}

public sealed class TeamRepository(GovernanceDbContext db) : ITeamRepository
{
    public async Task<IReadOnlyList<Team>> ListAsync(CancellationToken cancellationToken) =>
        await db.Teams.OrderBy(t => t.Name).ToListAsync(cancellationToken);

    public void Add(Team team) => db.Teams.Add(team);

    public void Remove(Team team) => db.Teams.Remove(team);
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
