using Atlas.Governance.Domain;

namespace Atlas.Governance.Application;

public interface ICostSourceRepository
{
    Task<IReadOnlyList<CostSource>> ListAsync(CancellationToken cancellationToken);

    Task<CostSource?> GetAsync(string provider, CancellationToken cancellationToken);

    void Add(CostSource source);

    void Remove(CostSource source);
}

public interface ICostFactRepository
{
    /// <summary>Idempotent sync: drop the source's facts inside [from, to] and store the fresh ones.</summary>
    Task ReplaceWindowAsync(Guid sourceId, DateOnly from, DateOnly to, IReadOnlyList<CostFact> facts, CancellationToken cancellationToken);

    Task<IReadOnlyList<CostFact>> ListAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

public interface ISeatFactRepository
{
    /// <summary>Seats are a snapshot: each sync replaces the source's previous snapshot.</summary>
    Task ReplaceAsync(Guid sourceId, IReadOnlyList<SeatFact> seats, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeatFact>> ListAsync(CancellationToken cancellationToken);
}

public interface IUsageFactRepository
{
    /// <summary>Replace-by-key upsert: (tenant, actor, tool, period, model). Re-running the CLI never double counts.</summary>
    Task UpsertAsync(IReadOnlyList<UsageFact> facts, CancellationToken cancellationToken);

    Task<IReadOnlyList<UsageFact>> ListAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>The tracked fact for one key, or null (telemetry increments add to it).</summary>
    Task<UsageFact?> GetAsync(string actor, string tool, DateOnly period, string model, CancellationToken cancellationToken);

    void Add(UsageFact fact);

    Task<IReadOnlyList<UsageFact>> ListForActorAsync(string actor, string tool, DateOnly period, CancellationToken cancellationToken);

    /// <summary>Replaces every fact of one tool inside the window (provider connectors that return per-member usage).</summary>
    Task ReplaceToolWindowAsync(string tool, DateOnly from, DateOnly to, IReadOnlyList<UsageFact> facts, CancellationToken cancellationToken);
}

public interface ITelemetryStreamRepository
{
    Task<IReadOnlyList<TelemetryStream>> GetAsync(IReadOnlyList<string> streamKeys, CancellationToken cancellationToken);

    void Add(TelemetryStream stream);

    Task PruneBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}

public interface IBudgetRepository
{
    Task<IReadOnlyList<Budget>> ListAsync(CancellationToken cancellationToken);

    void Add(Budget budget);

    void Remove(Budget budget);

    void AddAlert(BudgetAlert alert);

    Task<IReadOnlyList<BudgetAlert>> ListAlertsAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>Keys of alerts raised since <paramref name="sinceUtc"/> (deduplication).</summary>
    Task<HashSet<string>> AlertKeysAsync(DateOnly since, CancellationToken cancellationToken);
}

public interface ITeamRepository
{
    Task<IReadOnlyList<Team>> ListAsync(CancellationToken cancellationToken);

    void Add(Team team);

    void Remove(Team team);
}

public interface IUsageEventRepository
{
    void Add(UsageReportEvent evt);

    /// <summary>Events reported at or after <paramref name="sinceUtc"/>, oldest first.</summary>
    Task<IReadOnlyList<UsageReportEvent>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    Task PruneBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}

public interface IModelPriceRepository
{
    Task<IReadOnlyList<ModelPriceOverride>> ListAsync(CancellationToken cancellationToken);

    void Add(ModelPriceOverride price);

    void Remove(ModelPriceOverride price);
}

public interface IGovernanceUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>What one provider client collected for a window.</summary>
public sealed record CostCollection(IReadOnlyList<CostFact> Facts, IReadOnlyList<SeatFact> Seats)
{
    public static readonly CostCollection Empty = new([], []);

    /// <summary>Per-member daily token usage some providers expose (Cursor); replaced per window like the facts.</summary>
    public IReadOnlyList<UsageFact> Usage { get; init; } = [];
}

/// <summary>
/// Read-only client for one provider's billing/usage API. Receives the decrypted secret for the call
/// only; must never log it, store it or echo it in an error message.
/// </summary>
public interface ICostProviderClient
{
    string Provider { get; }

    Task<CostCollection> CollectAsync(CostSource source, string secret, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>A provider rejected the request in a way the operator must fix (bad key, missing scope, wrong org).</summary>
public sealed class CostProviderException(string message, Exception? inner = null) : Exception(message, inner);
