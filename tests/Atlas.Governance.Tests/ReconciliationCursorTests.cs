using System.Text.Json;
using Atlas.Governance.Application;
using Atlas.Governance.Domain;
using Atlas.Governance.Infrastructure.Providers;

namespace Atlas.Governance.Tests;

public class ReconciliationCursorTests
{
    private static readonly Guid Tenant = Atlas.Domain.Tenants.WellKnownTenants.DefaultId;
    private static readonly DateOnly Today = new(2026, 9, 20);

    private static UsageFact Usage(string provider, DateOnly day, decimal cost, string actor = "ana")
    {
        var f = new UsageFact(Guid.NewGuid(), Tenant, actor, "claude-code", day, provider == "anthropic" ? "claude-sonnet-4-5" : "gpt-4o", 1000, 0, 0, 0, 1, 1, cost, "v", "cli");
        f.SetProvider(provider);
        return f;
    }

    private static CostFact Billed(string provider, DateOnly day, decimal amount) =>
        new(Guid.NewGuid(), Tenant, Guid.NewGuid(), provider, CostBasis.ProviderReported, day, "line", "api", null, amount, "USD", null, null, "bill");

    [Fact]
    public void Reconciliation_compares_only_overlapping_days_and_gates_the_ratio_on_seven_days()
    {
        var usage = new List<UsageFact>();
        var costs = new List<CostFact>();
        for (var i = 0; i < 10; i++)
        {
            usage.Add(Usage("anthropic", Today.AddDays(-i), 10m));      // estimated 10/day
            costs.Add(Billed("anthropic", Today.AddDays(-i), 12m));     // billed 12/day → ratio 1.2
        }

        usage.Add(Usage("anthropic", Today.AddDays(-20), 10m));          // estimated only
        costs.Add(Billed("anthropic", Today.AddDays(-25), 30m));         // billed only
        for (var i = 0; i < 3; i++)
        {
            usage.Add(Usage("openai", Today.AddDays(-i), 5m));
            costs.Add(Billed("openai", Today.AddDays(-i), 4m));          // 3 days only → ratio present, not usable
        }

        costs.Add(Billed("github-copilot", Today, 190m));                // billed, never estimated

        var r = Reconciliations.Build(usage, costs, Today.AddDays(-30), Today);
        var anthropic = r.Providers.Single(p => p.Provider == "anthropic");
        Assert.Equal(10, anthropic.DaysWithBoth);
        Assert.Equal(1, anthropic.DaysEstimatedOnly);
        Assert.Equal(1, anthropic.DaysReportedOnly);
        Assert.Equal(1.2m, anthropic.Ratio);
        Assert.True(anthropic.RatioUsable);
        Assert.Equal(110m, anthropic.Estimated);
        Assert.Equal(150m, anthropic.Reported);
        Assert.Equal(100m, anthropic.EstimatedOnOverlap);
        Assert.Equal(120m, anthropic.ReportedOnOverlap);

        var openai = r.Providers.Single(p => p.Provider == "openai");
        Assert.Equal(0.8m, openai.Ratio);
        Assert.False(openai.RatioUsable);
        Assert.Contains("3 day(s) overlap", openai.Note);

        var copilot = r.Providers.Single(p => p.Provider == "github-copilot");
        Assert.Null(copilot.Ratio);
        Assert.Contains("no usage reports", copilot.Note);

        Assert.Equal(1.2m, r.BlendedRatio); // only usable providers blend
    }

    [Fact]
    public void Forecast_v2_uses_weekday_means_a_band_and_the_calibration_ratio()
    {
        // Four weeks of history: weekdays cost 10, weekends cost 2. Today is Sunday 20 Sep 2026 (day 20 of 30).
        var facts = new List<UsageFact>();
        for (var i = 0; i < 28; i++)
        {
            var day = Today.AddDays(-i);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (day.Month == 9 || i < 28)
            {
                facts.Add(Usage("anthropic", day, weekend ? 2m : 10m));
            }
        }

        var f = UsageForecasts.Build(facts, Today, calibrationRatio: 1.2m);
        Assert.Equal(20, f.DaysElapsedInMonth);
        // Remaining 21–30 Sep: Mon 21 … Wed 30 = 8 weekdays × 10 + 2 weekend days × 2 = 84 on top of month to date.
        Assert.Equal(f.MonthToDateCost + 84m, f.ProjectedMonthSeasonal);
        Assert.True(f.ProjectedMonthLow <= f.ProjectedMonthSeasonal);
        Assert.True(f.ProjectedMonthHigh >= f.ProjectedMonthSeasonal);
        Assert.True(f.ProjectedMonthLow >= f.MonthToDateCost);
        Assert.Equal(decimal.Round(f.ProjectedMonthSeasonal * 1.2m, 2), f.ProjectedMonthCalibrated);
        Assert.Equal(1.2m, f.CalibrationRatio);
        Assert.NotNull(f.CumulativeByDay);
        Assert.Equal(20, f.CumulativeByDay!.Count);
        Assert.Equal(f.MonthToDateCost, f.CumulativeByDay[^1]);
        Assert.True(f.CumulativeByDay.Zip(f.CumulativeByDay.Skip(1)).All(p => p.Second >= p.First)); // monotonic

        var flat = UsageForecasts.Build(facts, Today);
        Assert.Null(flat.ProjectedMonthCalibrated);
    }

    [Fact]
    public void Cursor_responses_parse_into_members_spend_and_usage_events()
    {
        using var members = JsonDocument.Parse("""{"teamMembers":[{"name":"Ana","email":"Ana@Example.com","role":"owner"},{"name":"Bruno","email":"bruno@example.com","role":"member"},{"name":"Nobody"}]}""");
        var m = CursorClient.ParseMembers(members.RootElement);
        Assert.Equal(2, m.Count);
        Assert.Equal(("ana@example.com", "owner"), m[0]);

        using var spend = JsonDocument.Parse("""{"teamMemberSpend":[{"spendCents":12345,"fastPremiumRequests":10,"name":"Ana","email":"ana@example.com","role":"owner"},{"spendCents":0,"email":"bruno@example.com"}],"subscriptionCycleStart":1756684800000,"totalMembers":2,"totalPages":1}""");
        var (rows, cycle) = CursorClient.ParseSpend(spend.RootElement);
        Assert.Equal(12345, rows.Single(r => r.Email == "ana@example.com").Cents);
        Assert.Equal(new DateOnly(2025, 9, 1), cycle);

        using var events = JsonDocument.Parse("""{"totalUsageEventsCount":2,"pagination":{"numPages":1,"currentPage":1,"pageSize":500,"hasNextPage":false,"hasPreviousPage":false},"usageEvents":[{"timestamp":"1757246400000","model":"claude-4-sonnet","kind":"Usage-based","maxMode":false,"requestsCosts":1,"isTokenBasedCall":true,"tokenUsage":{"inputTokens":1200,"outputTokens":300,"cacheWriteTokens":100,"cacheReadTokens":5000,"totalCents":3.5},"isFreeBypass":false,"userEmail":"ana@example.com"},{"timestamp":1757246460000,"model":"gpt-5","kind":"Included in Business","userEmail":"bruno@example.com"},{"timestamp":1757246460000,"model":"gpt-5"}]}""");
        var (list, more) = CursorClient.ParseUsageEvents(events.RootElement);
        Assert.False(more);
        Assert.Equal(2, list.Count); // the event without a user is skipped
        var ana = list.Single(e => e.Email == "ana@example.com");
        Assert.Equal(1200, ana.Input);
        Assert.Equal(5000, ana.CacheRead);
        Assert.Equal(4, ana.Cents); // 3.5 rounds to 4
        Assert.Equal("claude-4-sonnet", ana.Model);
        Assert.Equal(0, list.Single(e => e.Email == "bruno@example.com").Input); // included-in-plan call: no token block

        var key = Convert.FromBase64String(Convert.ToBase64String(new byte[32]));
        Assert.StartsWith("id-", CursorClient.Pseudonym(key, "Ana@Example.com"));
        Assert.Equal(CursorClient.Pseudonym(key, "Ana@Example.com"), CursorClient.Pseudonym(key, "ana@example.com"));
        Assert.Contains(CostProviders.Cursor, CostProviders.All);
    }
}
