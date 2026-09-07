using Atlas.Application.AiEstate;
using Atlas.Governance.Domain;

namespace Atlas.Governance.Application;

/// <summary>
/// Folds persisted cost/seat facts into the summary the portfolio report and the API show. Provider
/// totals are grouped by basis (ProviderReported vs Estimated) and never added across bases.
/// </summary>
public sealed class AiCostSummaryBuilder(ICostSourceRepository sources, ICostFactRepository facts, ISeatFactRepository seats) : IAiCostSummarySource
{
    private const int TopDimensions = 8;

    public async Task<AiCostSummary?> GetAsync(int days, CancellationToken cancellationToken)
    {
        var configured = await sources.ListAsync(cancellationToken);
        if (configured.Count == 0)
        {
            return null;
        }

        days = Math.Clamp(days <= 0 ? 30 : days, 1, 400);
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-days);
        var window = await facts.ListAsync(from, to, cancellationToken);
        var seatRows = await seats.ListAsync(cancellationToken);
        return Build(days, from, to, configured, window, seatRows, DateTimeOffset.UtcNow);
    }

    internal static AiCostSummary Build(int days, DateOnly from, DateOnly to, IReadOnlyList<CostSource> configured, IReadOnlyList<CostFact> window, IReadOnlyList<SeatFact> seatRows, DateTimeOffset now)
    {
        var bySource = configured.ToDictionary(s => s.Provider, StringComparer.Ordinal);
        var activityKeys = new HashSet<string>(StringComparer.Ordinal) { "active-developers", "sessions" };

        var providers = window
            .Where(f => !(f.Dimension == "claude-code" && activityKeys.Contains(f.DimensionKey)))
            .GroupBy(f => (f.Provider, f.Basis, f.Currency))
            .OrderBy(g => g.Key.Provider, StringComparer.Ordinal).ThenBy(g => g.Key.Basis)
            .Select(g =>
            {
                var source = bySource.GetValueOrDefault(g.Key.Provider);
                return new AiCostProviderSummary(
                    g.Key.Provider,
                    g.Key.Basis.ToString(),
                    g.Key.Currency,
                    decimal.Round(g.Sum(f => f.Amount), 2),
                    g.GroupBy(f => f.Detail is null ? f.DimensionKey : $"{f.DimensionKey} · {f.Detail}", StringComparer.Ordinal)
                        .Select(d => (d.Key, decimal.Round(d.Sum(f => f.Amount), 2)))
                        .OrderByDescending(d => d.Item2).ThenBy(d => d.Key, StringComparer.Ordinal)
                        .Take(TopDimensions)
                        .ToList(),
                    source?.LastSyncAtUtc, source?.LastSyncStatus, source?.LastSyncError);
            })
            .ToList();

        // Sources that never produced a fact still deserve a row, so the operator sees the sync error.
        foreach (var source in configured.Where(s => providers.All(p => p.Provider != s.Provider)).OrderBy(s => s.Provider, StringComparer.Ordinal))
        {
            providers.Add(new AiCostProviderSummary(source.Provider, "ProviderReported", "USD", 0m, [], source.LastSyncAtUtc, source.LastSyncStatus, source.LastSyncError));
        }

        var seatSummaries = seatRows
            .GroupBy(s => s.Provider, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AiSeatSummary(
                g.Key,
                g.Count(),
                g.Count(s => !s.IsIdle(now)),
                g.Count(s => s.IsIdle(now)),
                g.Count(s => s.PendingCancellation),
                g.Select(s => s.Plan).Where(p => p is not null).GroupBy(p => p).OrderByDescending(p => p.Count()).FirstOrDefault()?.Key))
            .ToList();

        var activity = window
            .Where(f => f.Dimension == "claude-code" && activityKeys.Contains(f.DimensionKey) && f.Quantity is not null)
            .GroupBy(f => (f.Provider, f.DimensionKey))
            .OrderBy(g => g.Key.Provider, StringComparer.Ordinal).ThenBy(g => g.Key.DimensionKey, StringComparer.Ordinal)
            .Select(g => new AiActivitySummary(g.Key.Provider, g.Key.DimensionKey, decimal.Round(g.Average(f => f.Quantity!.Value), 1), g.First().Unit ?? string.Empty))
            .ToList();

        return new AiCostSummary(days, from, to, providers, seatSummaries, activity, configured.Count);
    }
}
