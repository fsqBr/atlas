namespace Atlas.Application.AiEstate;

/// <summary>Spend of one provider under one basis over the window — bases are never merged.</summary>
public sealed record AiCostProviderSummary(
    string Provider,
    /// <summary>ProviderReported | Estimated.</summary>
    string Basis,
    string Currency,
    decimal Total,
    IReadOnlyList<(string Key, decimal Amount)> TopDimensions,
    DateTimeOffset? LastSyncAtUtc,
    string? LastSyncStatus,
    string? LastSyncError);

/// <summary>Assistant seats of one provider: how many are paid for, how many were used, how many sit idle.</summary>
public sealed record AiSeatSummary(string Provider, int Total, int ActiveWithinIdleWindow, int Idle, int PendingCancellation, string? Plan);

/// <summary>Aggregated activity counters (e.g. Claude Code active developers per day, averaged over the window). Never per person.</summary>
public sealed record AiActivitySummary(string Provider, string Key, decimal AveragePerDay, string Unit);

/// <summary>
/// What the portfolio report shows about AI spend. Produced by the governance module when cost sources
/// are connected; the core exposes only this port so it never depends on the module.
/// </summary>
public sealed record AiCostSummary(
    int Days,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<AiCostProviderSummary> Providers,
    IReadOnlyList<AiSeatSummary> Seats,
    IReadOnlyList<AiActivitySummary> Activity,
    int SourcesConfigured)
{
    public bool IsEmpty => Providers.Count == 0 && Seats.Count == 0 && Activity.Count == 0;
}

/// <summary>Port implemented by the governance module; the default returns null (no cost data, section hidden).</summary>
public interface IAiCostSummarySource
{
    Task<AiCostSummary?> GetAsync(int days, CancellationToken cancellationToken);
}

public sealed class NullAiCostSummarySource : IAiCostSummarySource
{
    public Task<AiCostSummary?> GetAsync(int days, CancellationToken cancellationToken) => Task.FromResult<AiCostSummary?>(null);
}
