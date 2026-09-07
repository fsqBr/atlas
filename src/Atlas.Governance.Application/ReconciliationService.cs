using Atlas.Governance.Domain;

namespace Atlas.Governance.Application;

/// <summary>
/// One provider, one window: what the catalog estimated from token counts next to what the provider's billing API
/// reported for the same days. The ratio (reported ÷ estimated over the days both exist) is the calibration factor
/// the forecasts can apply; it is only offered once enough overlapping days exist to mean something.
/// </summary>
public sealed record ProviderReconciliation(
    string Provider,
    decimal Estimated,
    decimal Reported,
    int DaysWithBoth,
    int DaysEstimatedOnly,
    int DaysReportedOnly,
    decimal? Ratio,
    bool RatioUsable,
    decimal EstimatedOnOverlap,
    decimal ReportedOnOverlap,
    string Note);

public sealed record Reconciliation(int Days, DateOnly From, DateOnly To, IReadOnlyList<ProviderReconciliation> Providers, decimal? BlendedRatio, string Method);

public static class Reconciliations
{
    /// <summary>Overlapping days needed before a ratio is applied to forecasts.</summary>
    public const int MinOverlapDays = 7;

    public static Reconciliation Build(IReadOnlyList<UsageFact> usage, IReadOnlyList<CostFact> costs, DateOnly from, DateOnly to)
    {
        var estimatedByProviderDay = usage
            .Where(u => u.EstimatedCost is not null)
            .GroupBy(u => (Provider: (u.Provider ?? ModelProviders.Guess(u.Model) ?? "unknown").ToLowerInvariant(), u.Period))
            .ToDictionary(g => g.Key, g => g.Sum(u => u.EstimatedCost!.Value));
        var reportedByProviderDay = costs
            .Where(c => c.Basis == CostBasis.ProviderReported)
            .GroupBy(c => (Provider: c.Provider.ToLowerInvariant(), c.Period))
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));

        var providers = estimatedByProviderDay.Keys.Select(k => k.Provider).Concat(reportedByProviderDay.Keys.Select(k => k.Provider)).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal);
        var rows = new List<ProviderReconciliation>();
        foreach (var provider in providers)
        {
            var est = estimatedByProviderDay.Where(kv => kv.Key.Provider == provider).ToDictionary(kv => kv.Key.Period, kv => kv.Value);
            var rep = reportedByProviderDay.Where(kv => kv.Key.Provider == provider).ToDictionary(kv => kv.Key.Period, kv => kv.Value);
            var both = est.Keys.Intersect(rep.Keys).ToList();
            var estOverlap = both.Sum(d => est[d]);
            var repOverlap = both.Sum(d => rep[d]);
            decimal? ratio = both.Count > 0 && estOverlap > 0 ? decimal.Round(repOverlap / estOverlap, 3) : null;
            var usable = ratio is not null && both.Count >= MinOverlapDays && ratio > 0;
            var note = rep.Count == 0 ? "No billing data for this provider — connect a cost source to reconcile."
                : est.Count == 0 ? "Billed, but no usage reports or telemetry name this provider — nothing to calibrate against."
                : both.Count < MinOverlapDays ? $"Only {both.Count} day(s) overlap; {MinOverlapDays} needed before the ratio is applied."
                : ratio > 1.5m ? "Billing is well above the estimate: usage outside the reports (other apps, seats, subscriptions) or higher contract prices."
                : ratio < 0.67m ? "Billing is well below the estimate: subscriptions or discounts cover part of the usage, or prices in the catalog are high."
                : "Estimate and billing agree within the expected range.";
            rows.Add(new ProviderReconciliation(provider, decimal.Round(est.Values.Sum(), 2), decimal.Round(rep.Values.Sum(), 2), both.Count, est.Keys.Except(rep.Keys).Count(), rep.Keys.Except(est.Keys).Count(), ratio, usable, decimal.Round(estOverlap, 2), decimal.Round(repOverlap, 2), note));
        }

        var usableRows = rows.Where(r => r.RatioUsable).ToList();
        decimal? blended = usableRows.Count > 0 && usableRows.Sum(r => r.EstimatedOnOverlap) > 0
            ? decimal.Round(usableRows.Sum(r => r.ReportedOnOverlap) / usableRows.Sum(r => r.EstimatedOnOverlap), 3)
            : null;
        return new Reconciliation(to.DayNumber - from.DayNumber, from, to, rows, blended, "reported ÷ estimated over days both exist; applied to forecasts once ≥ 7 days overlap");
    }
}

public sealed class ReconciliationService(IUsageFactRepository usage, ICostFactRepository costs)
{
    public async Task<Reconciliation> BuildAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days <= 0 ? 30 : days, 1, UsageReportService.MaxDaysBack);
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-days);
        var u = await usage.ListAsync(from, to, ct);
        var c = await costs.ListAsync(from, to, ct);
        return Reconciliations.Build(u, c, from, to);
    }
}
