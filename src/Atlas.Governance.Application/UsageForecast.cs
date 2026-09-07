using Atlas.Governance.Domain;

namespace Atlas.Governance.Application;

/// <summary>
/// Straight-line projections from stored daily facts — deliberately simple and explained in the UI: month-to-date
/// divided by days elapsed, and the last 7 days' pace extended over 30. Unpriced tokens are not money and never
/// enter these numbers; the unpriced share is reported next to them instead.
/// </summary>
public sealed record UsageForecast(
    decimal MonthToDateCost,
    int DaysElapsedInMonth,
    int DaysInMonth,
    decimal ProjectedMonthCost,
    decimal DailyAverage7,
    decimal DailyAverage30,
    decimal ProjectedNext30Cost,
    /// <summary>Percent change of the 7-day pace against the 30-day pace; null when there is no 30-day baseline.</summary>
    decimal? TrendPercent,
    long MonthToDateTokens,
    long UnpricedTokensMonthToDate);

public static class UsageForecasts
{
    public static UsageForecast Build(IEnumerable<UsageFact> facts, DateOnly today)
    {
        var rows = facts.ToList();
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var daysElapsed = today.Day; // today counts: its facts are in

        var month = rows.Where(f => f.Period >= monthStart && f.Period <= today).ToList();
        var mtd = month.Sum(f => f.EstimatedCost ?? 0m);
        var last7 = rows.Where(f => f.Period > today.AddDays(-7) && f.Period <= today).Sum(f => f.EstimatedCost ?? 0m);
        var last30 = rows.Where(f => f.Period > today.AddDays(-30) && f.Period <= today).Sum(f => f.EstimatedCost ?? 0m);
        var avg7 = last7 / 7m;
        var avg30 = last30 / 30m;
        var hasBaseline = rows.Any(f => f.Period <= today.AddDays(-7) && f.Period > today.AddDays(-30));

        return new UsageForecast(
            Round(mtd),
            daysElapsed,
            daysInMonth,
            Round(daysElapsed == 0 ? 0m : mtd / daysElapsed * daysInMonth),
            Round(avg7),
            Round(avg30),
            Round(avg7 * 30m),
            hasBaseline && avg30 > 0 ? Round((avg7 - avg30) / avg30 * 100m) : null,
            month.Sum(f => f.TotalTokens),
            month.Where(f => f.EstimatedCost is null).Sum(f => f.TotalTokens));
    }

    private static decimal Round(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Today as it happens: per actor, the last report and the day's totals; overall, an intraday curve.</summary>
public sealed record LiveActor(string Actor, IReadOnlyList<string> Tools, DateTimeOffset LastReportUtc, bool ActiveNow, long TokensToday, decimal EstimatedCostToday, int RequestsToday, long TokensLastHour);

public sealed record LivePoint(DateTimeOffset AtUtc, long Tokens, decimal EstimatedCost);

public sealed record LiveUsage(DateOnly Period, DateTimeOffset AsOfUtc, int ActiveMinutes, int ActiveActors, int ReportingActorsToday, long TokensToday, decimal EstimatedCostToday, long TokensLastHour, IReadOnlyList<LiveActor> Actors, IReadOnlyList<LivePoint> Curve);

public static class LiveUsages
{
    public const int BucketMinutes = 15;

    public static LiveUsage Build(IReadOnlyList<UsageReportEvent> events, DateOnly today, DateTimeOffset now, int activeMinutes)
    {
        var todays = events.Where(e => e.Period == today).OrderBy(e => e.ReportedAtUtc).ToList();
        var actors = todays
            .GroupBy(e => (e.Actor, e.Tool))
            .Select(g => g.Last())
            .GroupBy(e => e.Actor, StringComparer.Ordinal)
            .Select(g =>
            {
                var last = g.Max(e => e.ReportedAtUtc);
                var tokens = g.Sum(e => e.TokensToday);
                var hourAgo = now.AddHours(-1);
                var before = g.Sum(e => LatestAtOrBefore(todays, e.Actor, e.Tool, hourAgo)?.TokensToday ?? 0);
                return new LiveActor(
                    g.Key,
                    g.Select(e => e.Tool).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                    last,
                    now - last <= TimeSpan.FromMinutes(activeMinutes),
                    tokens,
                    decimal.Round(g.Sum(e => e.EstimatedCostToday ?? 0m), 2),
                    g.Sum(e => e.RequestsToday),
                    Math.Max(0, tokens - before));
            })
            .OrderByDescending(a => a.ActiveNow).ThenByDescending(a => a.TokensToday)
            .ToList();

        // Intraday curve: every 15 minutes from midnight UTC to now, the sum over (actor, tool) of the latest report.
        var curve = new List<LivePoint>();
        var start = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        if (todays.Count > 0)
        {
            var keys = todays.Select(e => (e.Actor, e.Tool)).Distinct().ToList();
            LivePoint PointAt(DateTimeOffset cut)
            {
                long tokens = 0;
                decimal cost = 0;
                foreach (var key in keys)
                {
                    var latest = LatestAtOrBefore(todays, key.Actor, key.Tool, cut);
                    if (latest is not null)
                    {
                        tokens += latest.TokensToday;
                        cost += latest.EstimatedCostToday ?? 0m;
                    }
                }

                return new LivePoint(cut, tokens, decimal.Round(cost, 2));
            }

            for (var at = start.AddMinutes(BucketMinutes); at < now; at = at.AddMinutes(BucketMinutes))
            {
                curve.Add(PointAt(at));
            }

            curve.Add(PointAt(now)); // the last point is always "now", whatever the bucket grid
        }

        return new LiveUsage(
            today, now, activeMinutes,
            actors.Count(a => a.ActiveNow),
            actors.Count,
            actors.Sum(a => a.TokensToday),
            decimal.Round(actors.Sum(a => a.EstimatedCostToday), 2),
            actors.Sum(a => a.TokensLastHour),
            actors, curve);
    }

    private static UsageReportEvent? LatestAtOrBefore(List<UsageReportEvent> ordered, string actor, string tool, DateTimeOffset at)
    {
        UsageReportEvent? found = null;
        foreach (var e in ordered)
        {
            if (e.ReportedAtUtc > at)
            {
                break;
            }

            if (e.Actor == actor && e.Tool == tool)
            {
                found = e;
            }
        }

        return found;
    }
}
