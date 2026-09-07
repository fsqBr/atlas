using Atlas.Application.Tenants;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Application;

/// <summary>One (period, model) line as the CLI reports it.</summary>
public sealed record UsageEntry(DateOnly Period, string Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, int Requests, int Sessions);

public sealed record UsageIngestResult(int Accepted, int Rejected, decimal EstimatedCost, int UnpricedEntries, string PriceCatalogVersion);

public sealed record UsageActorSummary(string Actor, IReadOnlyList<string> Tools, int Sessions, int Requests, long Tokens, decimal EstimatedCost, long UnpricedTokens, DateTimeOffset LastReportUtc, DateOnly LastPeriod, UsageForecast Forecast);

public sealed record UsageModelSummary(string Model, long Tokens, decimal? EstimatedCost, int Actors);

public sealed record UsageDaySummary(DateOnly Period, long Tokens, decimal EstimatedCost, int Actors);

public sealed record UsageProviderSummary(string Provider, long Tokens, decimal EstimatedCost, long UnpricedTokens, int Actors, int Models);

public sealed record UsageToolSummary(string Tool, long Tokens, decimal EstimatedCost, int Actors, string Source);

public sealed record AiUsageSummary(
    int Days,
    DateOnly From,
    DateOnly To,
    string PriceCatalogVersion,
    string Currency,
    int ReportingActors,
    long TotalTokens,
    decimal EstimatedCost,
    long UnpricedTokens,
    IReadOnlyList<UsageActorSummary> Actors,
    IReadOnlyList<UsageModelSummary> ByModel,
    IReadOnlyList<UsageDaySummary> ByDay,
    UsageForecast Forecast,
    IReadOnlyList<UsageProviderSummary> ByProvider,
    IReadOnlyList<UsageToolSummary> ByTool,
    IReadOnlyList<TeamSpend> ByTeam);

/// <summary>
/// Ingests developer-CLI usage reports (opt-in, self-declared actor, aggregates only) and folds them into the
/// usage summary. Idempotent per (actor, tool, period, model): re-running the CLI replaces the day's line.
/// </summary>
public sealed class UsageReportService(
    IUsageFactRepository facts,
    IPriceCatalogResolver priceResolver,
    IGovernanceUnitOfWork unitOfWork,
    ITenantContext tenant,
    ILogger<UsageReportService> logger,
    IUsageEventRepository? events = null,
    BudgetService? budgets = null,
    ITeamRepository? teams = null)
{
    public const int MaxEntriesPerReport = 2000;
    public const int MaxDaysBack = 400;

    public async Task<UsageIngestResult> IngestAsync(string actor, string tool, string source, IReadOnlyList<UsageEntry> entries, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("actor is required: who ran the CLI (a name, handle or pseudonym).", nameof(actor));
        }

        if (string.IsNullOrWhiteSpace(tool))
        {
            throw new ArgumentException("tool is required (e.g. claude-code).", nameof(tool));
        }

        if (entries.Count > MaxEntriesPerReport)
        {
            throw new ArgumentException($"At most {MaxEntriesPerReport} entries per report.", nameof(entries));
        }

        var prices = await priceResolver.ResolveAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = new List<UsageFact>();
        var rejected = 0;
        var unpriced = 0;
        decimal cost = 0;
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Model) || e.Period > today.AddDays(1) || e.Period < today.AddDays(-MaxDaysBack)
                || e.InputTokens < 0 || e.OutputTokens < 0 || e.CacheReadTokens < 0 || e.CacheWriteTokens < 0 || e.Requests < 0 || e.Sessions < 0)
            {
                rejected++;
                continue;
            }

            var estimate = prices.Estimate(e.Model, e.InputTokens, e.OutputTokens, e.CacheReadTokens, e.CacheWriteTokens);
            if (estimate is null)
            {
                unpriced++;
            }
            else
            {
                cost += estimate.Value;
            }

            rows.Add(new UsageFact(Guid.NewGuid(), tenant.Require(), actor, tool, e.Period, e.Model, e.InputTokens, e.OutputTokens, e.CacheReadTokens, e.CacheWriteTokens, e.Requests, e.Sessions, estimate, prices.Version, source));
        }

        // Two lines for the same (period, model) in one report are merged before the upsert.
        var merged = rows
            .GroupBy(r => (r.Period, r.Model), comparer: null)
            .Select(g => g.Count() == 1 ? g.First() : new UsageFact(Guid.NewGuid(), tenant.Require(), actor, tool, g.Key.Period, g.Key.Model,
                g.Sum(x => x.InputTokens), g.Sum(x => x.OutputTokens), g.Sum(x => x.CacheReadTokens), g.Sum(x => x.CacheWriteTokens), g.Sum(x => x.Requests), g.Max(x => x.Sessions),
                g.Any(x => x.EstimatedCost is null) ? null : g.Sum(x => x.EstimatedCost!.Value), prices.Version, source))
            .ToList();

        await facts.UpsertAsync(merged, cancellationToken);
        if (events is not null)
        {
            // The day's running totals at this moment feed the live view; older events are pruned on the way.
            var todays = merged.Where(r => r.Period == today).ToList();
            if (todays.Count > 0)
            {
                var now = DateTimeOffset.UtcNow;
                events.Add(new UsageReportEvent(Guid.NewGuid(), tenant.Require(), todays[0].Actor, todays[0].Tool, today, now,
                    todays.Sum(r => r.TotalTokens), todays.Any(r => r.EstimatedCost is null) && todays.All(r => r.EstimatedCost is null) ? null : todays.Sum(r => r.EstimatedCost ?? 0m), todays.Sum(r => r.Requests)));
                await events.PruneBeforeAsync(now.AddDays(-UsageReportEvent.RetentionDays), cancellationToken);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (budgets is not null)
        {
            await budgets.EvaluateAsync(cancellationToken);
        }

        logger.LogInformation("Usage report from {Actor}/{Tool}: {Accepted} line(s) accepted, {Rejected} rejected, est. {Cost} {Currency}.", actor, tool, merged.Count, rejected, decimal.Round(cost, 2), prices.Currency);
        return new UsageIngestResult(merged.Count, rejected, decimal.Round(cost, 2), unpriced, prices.Version);
    }

    public async Task<AiUsageSummary?> SummaryAsync(int days, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days <= 0 ? 30 : days, 1, MaxDaysBack);
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-days);
        // Forecasts need the month so far and a 30-day baseline even when the window is shorter.
        var monthStart = new DateOnly(to.Year, to.Month, 1);
        var loadFrom = new[] { from, monthStart, to.AddDays(-30) }.Min();
        var all = await facts.ListAsync(loadFrom, to, cancellationToken);
        var rows = all.Where(r => r.Period >= from).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var prices = await priceResolver.ResolveAsync(cancellationToken);
        var teamList = teams is null ? [] : await teams.ListAsync(cancellationToken);
        return Build(days, from, to, rows, prices, all, teamList);
    }

    /// <summary>Today as it happens, from the report events of the last 24 hours. Null when nothing was reported today.</summary>
    public async Task<LiveUsage?> LiveAsync(int activeMinutes, CancellationToken cancellationToken)
    {
        if (events is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var recent = await events.ListSinceAsync(now.AddHours(-26), cancellationToken);
        return recent.Any(e => e.Period == today) ? LiveUsages.Build(recent, today, now, Math.Clamp(activeMinutes <= 0 ? 15 : activeMinutes, 1, 24 * 60)) : null;
    }

    internal static AiUsageSummary Build(int days, DateOnly from, DateOnly to, IReadOnlyList<UsageFact> rows, PriceCatalog prices, IReadOnlyList<UsageFact>? history = null, IReadOnlyList<Team>? teamList = null)
    {
        history ??= rows;
        teamList ??= [];
        var monthStart = new DateOnly(to.Year, to.Month, 1);
        var actors = rows
            .GroupBy(r => r.Actor, StringComparer.Ordinal)
            .Select(g => new UsageActorSummary(
                g.Key,
                g.Select(r => r.Tool).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                g.GroupBy(r => r.Period).Sum(d => d.Max(r => r.Sessions)),
                g.Sum(r => r.Requests),
                g.Sum(r => r.TotalTokens),
                decimal.Round(g.Sum(r => r.EstimatedCost ?? 0m), 2),
                g.Where(r => r.EstimatedCost is null).Sum(r => r.TotalTokens),
                g.Max(r => r.ReportedAtUtc),
                g.Max(r => r.Period),
                UsageForecasts.Build(history.Where(h => h.Actor == g.Key), to)))
            .OrderByDescending(a => a.EstimatedCost).ThenByDescending(a => a.Tokens)
            .ToList();

        var byModel = rows
            .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UsageModelSummary(g.Key, g.Sum(r => r.TotalTokens), g.Any(r => r.EstimatedCost is null) ? null : decimal.Round(g.Sum(r => r.EstimatedCost!.Value), 2), g.Select(r => r.Actor).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(m => m.EstimatedCost ?? -1).ThenByDescending(m => m.Tokens)
            .ToList();

        var byDay = rows
            .GroupBy(r => r.Period)
            .OrderBy(g => g.Key)
            .Select(g => new UsageDaySummary(g.Key, g.Sum(r => r.TotalTokens), decimal.Round(g.Sum(r => r.EstimatedCost ?? 0m), 2), g.Select(r => r.Actor).Distinct(StringComparer.Ordinal).Count()))
            .ToList();

        return new AiUsageSummary(
            days, from, to, prices.Version, prices.Currency,
            actors.Count,
            rows.Sum(r => r.TotalTokens),
            decimal.Round(rows.Sum(r => r.EstimatedCost ?? 0m), 2),
            rows.Where(r => r.EstimatedCost is null).Sum(r => r.TotalTokens),
            actors, byModel, byDay,
            UsageForecasts.Build(history, to),
            rows.GroupBy(r => r.Provider ?? ModelProviders.Guess(r.Model) ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .Select(g => new UsageProviderSummary(g.Key, g.Sum(r => r.TotalTokens), decimal.Round(g.Sum(r => r.EstimatedCost ?? 0m), 2), g.Where(r => r.EstimatedCost is null).Sum(r => r.TotalTokens), g.Select(r => r.Actor).Distinct(StringComparer.Ordinal).Count(), g.Select(r => r.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
                .OrderByDescending(p => p.EstimatedCost).ThenByDescending(p => p.Tokens).ToList(),
            rows.GroupBy(r => r.Tool, StringComparer.OrdinalIgnoreCase)
                .Select(g => new UsageToolSummary(g.Key, g.Sum(r => r.TotalTokens), decimal.Round(g.Sum(r => r.EstimatedCost ?? 0m), 2), g.Select(r => r.Actor).Distinct(StringComparer.Ordinal).Count(), g.Select(r => r.Source).Distinct().OrderBy(x => x).Aggregate((a, b) => a + "+" + b)))
                .OrderByDescending(t => t.EstimatedCost).ThenByDescending(t => t.Tokens).ToList(),
            BudgetService.ByTeam(teamList, rows, history.Where(h => h.Period >= monthStart).ToList(), to));
    }
}
