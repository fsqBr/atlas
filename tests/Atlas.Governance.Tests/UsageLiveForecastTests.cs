using Atlas.Agent;
using Atlas.Governance.Application;
using Atlas.Governance.Domain;

namespace Atlas.Governance.Tests;

public class UsageLiveForecastTests
{
    private static readonly Guid Tenant = Atlas.Domain.Tenants.WellKnownTenants.DefaultId;

    private static UsageFact Fact(string actor, DateOnly day, decimal? cost, long tokens = 1000) =>
        new(Guid.NewGuid(), Tenant, actor, "claude-code", day, "m", tokens, 0, 0, 0, 1, 1, cost, "v", "cli");

    [Fact]
    public void Forecast_projects_month_from_days_elapsed_and_next_30_from_the_7_day_pace()
    {
        var today = new DateOnly(2026, 9, 10); // 10 days elapsed of 30
        var facts = new List<UsageFact>();
        for (var d = 0; d < 10; d++)
        {
            facts.Add(Fact("ana", today.AddDays(-d), 10m)); // 10 USD/day this month
        }

        for (var d = 10; d < 30; d++)
        {
            facts.Add(Fact("ana", today.AddDays(-d), 4m)); // 4 USD/day before that
        }

        facts.Add(Fact("ana", today, null, 500)); // unpriced tokens: counted as tokens, never as money

        var f = UsageForecasts.Build(facts, today);
        Assert.Equal(100m, f.MonthToDateCost);
        Assert.Equal(10, f.DaysElapsedInMonth);
        Assert.Equal(30, f.DaysInMonth);
        Assert.Equal(300m, f.ProjectedMonthCost);
        Assert.Equal(10m, f.DailyAverage7);
        Assert.Equal(6m, f.DailyAverage30); // 10 days × 10 + 20 days × 4 = 180 over 30 days
        Assert.Equal(300m, f.ProjectedNext30Cost);
        Assert.Equal(66.67m, f.TrendPercent); // 7-day pace 10 vs 30-day pace 6 → +66.67 %
        Assert.Equal(10_500, f.MonthToDateTokens);
        Assert.Equal(500, f.UnpricedTokensMonthToDate);
    }

    [Fact]
    public void Forecast_has_no_trend_without_a_baseline()
    {
        var today = new DateOnly(2026, 9, 3);
        var f = UsageForecasts.Build([Fact("ana", today, 5m), Fact("ana", today.AddDays(-1), 5m)], today);
        Assert.Equal(10m, f.MonthToDateCost);
        Assert.Equal(100m, f.ProjectedMonthCost); // 10 / 3 × 30
        Assert.Null(f.TrendPercent);
    }

    [Fact]
    public void Live_view_sums_latest_report_per_actor_and_tool_marks_active_and_builds_the_curve()
    {
        var today = new DateOnly(2026, 9, 7);
        var now = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        UsageReportEvent E(string actor, string tool, int hour, int minute, long tokens, decimal cost) =>
            new(Guid.NewGuid(), Tenant, actor, tool, today, new DateTimeOffset(2026, 9, 7, hour, minute, 0, TimeSpan.Zero), tokens, cost, 1);

        var events = new List<UsageReportEvent>
        {
            E("ana", "claude-code", 8, 0, 1000, 1m),
            E("ana", "claude-code", 9, 30, 5000, 5m),   // running total replaces the earlier one
            E("ana", "codex", 9, 45, 200, 0.2m),
            E("bruno", "claude-code", 7, 0, 3000, 3m),  // last report 3 h ago → not active
            new(Guid.NewGuid(), Tenant, "carla", "claude-code", today.AddDays(-1), new DateTimeOffset(2026, 9, 6, 23, 0, 0, TimeSpan.Zero), 9999, 9m, 1), // yesterday: ignored
        };

        var live = LiveUsages.Build(events, today, now, activeMinutes: 60);
        Assert.Equal(2, live.ReportingActorsToday);
        Assert.Equal(1, live.ActiveActors);
        Assert.Equal(8200, live.TokensToday);
        Assert.Equal(8.2m, live.EstimatedCostToday);
        var ana = live.Actors.Single(a => a.Actor == "ana");
        Assert.True(ana.ActiveNow);
        Assert.Equal(["claude-code", "codex"], ana.Tools);
        Assert.Equal(5200, ana.TokensToday);
        Assert.Equal(4200, ana.TokensLastHour); // 5200 now vs 1000 at 09:00
        Assert.False(live.Actors.Single(a => a.Actor == "bruno").ActiveNow);
        Assert.Equal(0, live.Actors.Single(a => a.Actor == "bruno").TokensLastHour);

        // Curve: 15-minute buckets from 00:15 to 10:00; at 07:15 only bruno counts, at 10:00 everything.
        Assert.Equal(40, live.Curve.Count);
        Assert.Equal(0, live.Curve.First(p => p.AtUtc.Hour == 6 && p.AtUtc.Minute == 45).Tokens);
        Assert.Equal(3000, live.Curve.First(p => p.AtUtc.Hour == 7 && p.AtUtc.Minute == 15).Tokens);
        Assert.Equal(8200, live.Curve.Last().Tokens);
        Assert.Equal(now, live.Curve.Last().AtUtc);
    }

    [Fact]
    public void Codex_rollouts_fold_token_count_events_by_day_and_model()
    {
        var agg = new Dictionary<(DateOnly, string), ClaudeCodeUsageReader.Acc>();
        var lines = new[]
        {
            """{"timestamp":"2026-09-07T09:00:00.000Z","type":"session_meta","payload":{"id":"sess-1","cwd":"C:\\secret"}}""",
            """{"timestamp":"2026-09-07T09:00:01.000Z","type":"turn_context","payload":{"model":"gpt-5-codex","cwd":"C:\\secret"}}""",
            """{"timestamp":"2026-09-07T09:00:05.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1200,"cached_input_tokens":1000,"output_tokens":50,"reasoning_output_tokens":10,"total_tokens":1250},"last_token_usage":{"input_tokens":1200,"cached_input_tokens":1000,"output_tokens":50,"reasoning_output_tokens":10,"total_tokens":1250}}}}""",
            """{"timestamp":"2026-09-07T09:01:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null}}""",
            """{"timestamp":"2026-09-07T09:02:00.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":300,"cached_input_tokens":0,"output_tokens":20}}}}""",
            """{"timestamp":"2026-09-07T09:03:00.000Z","type":"event_msg","payload":{"type":"agent_message","message":"hello"}}""",
            """{"timestamp":"2026-08-01T09:03:00.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":5,"output_tokens":5}}}}""",
            "garbage",
        };
        CodexSource.ReadLines(lines, "rollout-file", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 7), agg);

        var acc = Assert.Single(agg);
        Assert.Equal((new DateOnly(2026, 9, 7), "gpt-5-codex"), acc.Key);
        Assert.Equal(500, acc.Value.Input);      // 200 fresh + 300
        Assert.Equal(1000, acc.Value.CacheRead); // cached split out of input
        Assert.Equal(70, acc.Value.Output);
        Assert.Equal(2, acc.Value.Requests);
        Assert.Equal(["sess-1"], acc.Value.Sessions);
    }

    [Theory]
    [InlineData("5m", 5)]
    [InlineData("30min", 30)]
    [InlineData("2h", 120)]
    [InlineData("12", 720)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData("0m", null)]
    public void Interval_parsing_accepts_minutes_hours_and_bare_hours(string text, int? expected)
    {
        Assert.Equal(expected, AgentConfig.ParseIntervalMinutes(text));
    }

    [Fact]
    public void Scheduler_supports_minute_intervals()
    {
        var exe = "/usr/local/bin/atlas-agent";
        Assert.StartsWith("*/5 * * * * ", Scheduler.CronLine(exe, 5, "/tmp/a.log"));
        Assert.StartsWith("17 */2 * * * ", Scheduler.CronLine(exe, 120, "/tmp/a.log"));
        var args = Scheduler.SchtasksCreateArgs(exe, 15);
        Assert.Equal("MINUTE", args[Array.IndexOf(args, "/SC") + 1]);
        Assert.Equal("15", args[Array.IndexOf(args, "/MO") + 1]);
        Assert.Contains("<integer>300</integer>", Scheduler.LaunchdPlist(exe, 5, "/tmp/a.log"));
        Assert.Equal("5 min", Scheduler.Describe(5));
        Assert.Equal("12 h", Scheduler.Describe(720));
    }
}
