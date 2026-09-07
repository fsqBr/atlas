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

public interface IGovernanceUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>What one provider client collected for a window.</summary>
public sealed record CostCollection(IReadOnlyList<CostFact> Facts, IReadOnlyList<SeatFact> Seats)
{
    public static readonly CostCollection Empty = new([], []);
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
