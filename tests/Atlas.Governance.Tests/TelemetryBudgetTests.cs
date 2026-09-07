using System.Text.Json;
using Atlas.Application.Tenants;
using Atlas.Governance.Application;
using Atlas.Governance.Application.Telemetry;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Governance.Tests;

public class TelemetryBudgetTests
{
    private static readonly Guid Tenant = Atlas.Domain.Tenants.WellKnownTenants.DefaultId;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    // ---- fakes ----

    private sealed class Facts : IUsageFactRepository
    {
        public List<UsageFact> Items { get; } = [];

        public Task UpsertAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct)
        {
            foreach (var f in facts)
            {
                Items.RemoveAll(x => x.Actor == f.Actor && x.Tool == f.Tool && x.Period == f.Period && x.Model == f.Model);
                Items.Add(f);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UsageFact>> ListAsync(DateOnly from, DateOnly to, CancellationToken ct) => Task.FromResult<IReadOnlyList<UsageFact>>(Items.Where(i => i.Period >= from && i.Period <= to).ToList());

        public Task<UsageFact?> GetAsync(string actor, string tool, DateOnly period, string model, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(i => i.Actor == actor && i.Tool == tool && i.Period == period && i.Model == model));

        public void Add(UsageFact fact) => Items.Add(fact);

        public Task<IReadOnlyList<UsageFact>> ListForActorAsync(string actor, string tool, DateOnly period, CancellationToken ct) => Task.FromResult<IReadOnlyList<UsageFact>>(Items.Where(i => i.Actor == actor && i.Tool == tool && i.Period == period).ToList());

        public Task ReplaceToolWindowAsync(string tool, DateOnly from, DateOnly to, IReadOnlyList<UsageFact> facts, CancellationToken ct)
        {
            Items.RemoveAll(i => i.Tool == tool && i.Period >= from && i.Period <= to);
            Items.AddRange(facts);
            return Task.CompletedTask;
        }
    }

    private sealed class Streams : ITelemetryStreamRepository
    {
        public List<TelemetryStream> Items { get; } = [];

        public Task<IReadOnlyList<TelemetryStream>> GetAsync(IReadOnlyList<string> keys, CancellationToken ct) => Task.FromResult<IReadOnlyList<TelemetryStream>>(Items.Where(s => keys.Contains(s.StreamKey)).ToList());

        public void Add(TelemetryStream stream) => Items.Add(stream);

        public Task PruneBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Events : IUsageEventRepository
    {
        public List<UsageReportEvent> Items { get; } = [];

        public void Add(UsageReportEvent evt) => Items.Add(evt);

        public Task<IReadOnlyList<UsageReportEvent>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken ct) => Task.FromResult<IReadOnlyList<UsageReportEvent>>(Items.Where(e => e.ReportedAtUtc >= sinceUtc).ToList());

        public Task PruneBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Budgets : IBudgetRepository
    {
        public List<Budget> Items { get; } = [];

        public List<BudgetAlert> Alerts { get; } = [];

        public Task<IReadOnlyList<Budget>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Budget>>(Items.ToList());

        public void Add(Budget budget) => Items.Add(budget);

        public void Remove(Budget budget) => Items.Remove(budget);

        public void AddAlert(BudgetAlert alert) => Alerts.Add(alert);

        public Task<IReadOnlyList<BudgetAlert>> ListAlertsAsync(DateTimeOffset sinceUtc, CancellationToken ct) => Task.FromResult<IReadOnlyList<BudgetAlert>>(Alerts.ToList());

        public Task<HashSet<string>> AlertKeysAsync(DateOnly since, CancellationToken ct) => Task.FromResult(Alerts.Select(a => a.Key).ToHashSet(StringComparer.Ordinal));
    }

    private sealed class Teams : ITeamRepository
    {
        public List<Team> Items { get; } = [];

        public Task<IReadOnlyList<Team>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Team>>(Items.ToList());

        public void Add(Team team) => Items.Add(team);

        public void Remove(Team team) => Items.Remove(team);
    }

    private sealed class Sink : IAlertSink
    {
        public List<string> Sent { get; } = [];

        public List<AlertChannels?> Channels { get; } = [];

        public Task<string?> SendAsync(Guid tenantId, string title, string body, AlertChannels? channels, CancellationToken ct)
        {
            Sent.Add(title + " | " + body);
            Channels.Add(channels);
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class UnitOfWork : IGovernanceUnitOfWork
    {
        public int Saves;

        public Task SaveChangesAsync(CancellationToken ct)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedResolver(PriceCatalog c) : IPriceCatalogResolver
    {
        public Task<PriceCatalog> ResolveAsync(CancellationToken ct) => Task.FromResult(c);
    }

    private static (TelemetryIngestService Service, Facts Facts, Streams Streams, Events Events, Budgets Budgets, Sink Sink) Ingest(string actorMode = "pseudonym")
    {
        var facts = new Facts();
        var streams = new Streams();
        var events = new Events();
        var budgets = new Budgets();
        var sink = new Sink();
        var uow = new UnitOfWork();
        var budgetService = new BudgetService(budgets, new Teams(), facts, sink, uow, SystemTenantContext.Instance, NullLogger<BudgetService>.Instance);
        var service = new TelemetryIngestService(facts, streams, events, new FixedResolver(PriceCatalog.LoadBuiltin()), budgetService, uow, SystemTenantContext.Instance,
            new TelemetryOptions { ActorMode = actorMode, HmacKeyBase64 = Convert.ToBase64String(new byte[32]) }, NullLogger<TelemetryIngestService>.Instance);
        return (service, facts, streams, events, budgets, sink);
    }

    private static string Nanos(DateTimeOffset at) => (at.ToUnixTimeMilliseconds() * 1_000_000L).ToString();

    private static string ClaudeCodeMetrics(long input, long output, long cacheRead, int temporality, string start, string time, string email = "ana@example.com")
    {
        const string dp = """{"attributes":[{"key":"type","value":{"stringValue":"@type@"}},{"key":"model","value":{"stringValue":"claude-sonnet-4-5"}},{"key":"user.email","value":{"stringValue":"@email@"}},{"key":"session.id","value":{"stringValue":"s-1"}}],"startTimeUnixNano":"@start@","timeUnixNano":"@time@","asInt":"@value@"}""";
        string Dp(string type, long value) => dp.Replace("@type@", type).Replace("@email@", email).Replace("@start@", start).Replace("@time@", time).Replace("@value@", value.ToString());
        const string doc = """{"resourceMetrics":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"claude-code"}}]},"scopeMetrics":[{"metrics":[{"name":"claude_code.token.usage","sum":{"aggregationTemporality":@temp@,"isMonotonic":true,"dataPoints":[@dps@]}},{"name":"claude_code.cost.usage","sum":{"aggregationTemporality":@temp@,"isMonotonic":true,"dataPoints":[{"attributes":[{"key":"model","value":{"stringValue":"claude-sonnet-4-5"}},{"key":"user.email","value":{"stringValue":"@email@"}}],"startTimeUnixNano":"@start@","timeUnixNano":"@time@","asDouble":0.42}]}},{"name":"claude_code.lines_of_code.count","sum":{"aggregationTemporality":@temp@,"dataPoints":[{"attributes":[],"timeUnixNano":"@time@","asInt":"12"}]}}]}]}]}""";
        return doc.Replace("@temp@", temporality.ToString()).Replace("@dps@", string.Join(",", Dp("input", input), Dp("output", output), Dp("cacheRead", cacheRead))).Replace("@email@", email).Replace("@start@", start).Replace("@time@", time);
    }

    [Fact]
    public void Parser_reads_claude_code_delta_metrics_and_ignores_the_rest()
    {
        var now = DateTimeOffset.UtcNow;
        using var doc = JsonDocument.Parse(ClaudeCodeMetrics(1000, 200, 5000, temporality: 1, Nanos(now.AddMinutes(-1)), Nanos(now)));
        var r = OtlpJsonParser.ParseMetrics(doc.RootElement);
        Assert.Empty(r.Cumulative);
        Assert.Equal(1, r.Ignored); // lines_of_code
        Assert.Equal(4, r.Deltas.Count); // 3 token types + cost
        var input = r.Deltas.Single(d => d.InputTokens > 0);
        Assert.Equal("ana@example.com", input.RawActor);
        Assert.Equal(ActorKind.Person, input.ActorKind);
        Assert.Equal("claude-code", input.Tool);
        Assert.Equal("claude-sonnet-4-5", input.Model);
        Assert.Equal(5000, r.Deltas.Single(d => d.CacheReadTokens > 0).CacheReadTokens);
        Assert.Equal(0.42m, r.Deltas.Single(d => d.ReportedCost is not null).ReportedCost);
    }

    [Fact]
    public async Task Cumulative_counters_become_increments_across_batches_and_people_are_pseudonymised()
    {
        var (service, facts, streams, events, _, _) = Ingest();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        using (var first = JsonDocument.Parse(ClaudeCodeMetrics(1000, 100, 0, temporality: 2, Nanos(start), Nanos(start.AddMinutes(1)))))
        {
            var r = await service.IngestMetricsAsync(first.RootElement, CancellationToken.None);
            Assert.Equal(1, r.Accepted);
        }

        using (var second = JsonDocument.Parse(ClaudeCodeMetrics(1500, 130, 0, temporality: 2, Nanos(start), Nanos(start.AddMinutes(2)))))
        {
            await service.IngestMetricsAsync(second.RootElement, CancellationToken.None);
        }

        var fact = Assert.Single(facts.Items);
        Assert.Equal(1500, fact.InputTokens); // 1000 + (1500 − 1000)
        Assert.Equal(130, fact.OutputTokens);
        Assert.StartsWith("id-", fact.Actor); // never the e-mail by default
        Assert.DoesNotContain("example.com", fact.Actor);
        Assert.Equal("otel", fact.Source);
        Assert.Equal("anthropic", fact.Provider);
        Assert.NotNull(fact.EstimatedCost); // Sonnet is priced
        Assert.Equal(0.42m, fact.ReportedCost); // the cost counter did not move between the two batches → no second increment
        Assert.Equal(4, streams.Items.Count); // three token streams + cost stream
        Assert.Equal(2, events.Items.Count); // one live event per batch

        // A reset (process restarted, counter back to a small value) counts the new value, never a negative delta.
        var restart = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var third = JsonDocument.Parse(ClaudeCodeMetrics(50, 5, 0, temporality: 2, Nanos(restart), Nanos(restart.AddSeconds(30)))))
        {
            await service.IngestMetricsAsync(third.RootElement, CancellationToken.None);
        }

        Assert.Equal(1550, facts.Items.Single().InputTokens);
        Assert.Equal(0.84m, facts.Items.Single().ReportedCost); // the restarted counter's 0.42 is new spend
    }

    [Fact]
    public async Task Label_mode_keeps_the_identity_and_services_are_prefixed()
    {
        var (service, facts, _, _, _, _) = Ingest(actorMode: "label");
        var now = DateTimeOffset.UtcNow;
        const string spansDoc = """{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"payments-api"}}]},"scopeSpans":[{"spans":[{"name":"chat gpt-4o","startTimeUnixNano":"@now@","attributes":[{"key":"gen_ai.system","value":{"stringValue":"openai"}},{"key":"gen_ai.request.model","value":{"stringValue":"gpt-4o"}},{"key":"gen_ai.usage.input_tokens","value":{"intValue":"1200"}},{"key":"gen_ai.usage.output_tokens","value":{"intValue":"300"}}]},{"name":"db query","startTimeUnixNano":"@now@","attributes":[]}]}]}]}""";
        var spans = spansDoc.Replace("@now@", Nanos(now));
        using var doc = JsonDocument.Parse(spans);
        var r = await service.IngestTracesAsync(doc.RootElement, CancellationToken.None);
        Assert.Equal(1, r.Accepted);
        Assert.Equal(1, r.Ignored);
        var fact = Assert.Single(facts.Items);
        Assert.Equal("svc:payments-api", fact.Actor);
        Assert.Equal("payments-api", fact.Tool);
        Assert.Equal("openai", fact.Provider);
        Assert.Equal(1200, fact.InputTokens);
        Assert.Equal(1, fact.Requests);

        using var metrics = JsonDocument.Parse(ClaudeCodeMetrics(10, 10, 0, temporality: 1, Nanos(now), Nanos(now), email: "bruno@example.com"));
        await service.IngestMetricsAsync(metrics.RootElement, CancellationToken.None);
        Assert.Contains(facts.Items, f => f.Actor == "bruno@example.com");
    }

    [Fact]
    public void Gen_ai_histograms_carry_tokens_in_sum_and_requests_in_count()
    {
        var now = DateTimeOffset.UtcNow;
        const string histDoc = """{"resourceMetrics":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"litellm"}}]},"scopeMetrics":[{"metrics":[{"name":"gen_ai.client.token.usage","histogram":{"aggregationTemporality":"AGGREGATION_TEMPORALITY_DELTA","dataPoints":[{"attributes":[{"key":"gen_ai.token.type","value":{"stringValue":"input"}},{"key":"gen_ai.request.model","value":{"stringValue":"gpt-4o-mini"}},{"key":"gen_ai.provider.name","value":{"stringValue":"openai"}}],"timeUnixNano":"@now@","count":"7","sum":21000.0}]}}]}]}]}""";
        var json = histDoc.Replace("@now@", Nanos(now));
        using var doc = JsonDocument.Parse(json);
        var r = OtlpJsonParser.ParseMetrics(doc.RootElement);
        var d = Assert.Single(r.Deltas);
        Assert.Equal(21000, d.InputTokens);
        Assert.Equal(7, d.Requests);
        Assert.Equal("litellm", d.Tool);
        Assert.Equal(ActorKind.Service, d.ActorKind);
    }

    [Fact]
    public async Task Budgets_alert_once_per_threshold_per_month_and_flag_anomalies()
    {
        var facts = new Facts();
        var budgetsRepo = new Budgets();
        var teams = new Teams();
        var sink = new Sink();
        var service = new BudgetService(budgetsRepo, teams, facts, sink, new UnitOfWork(), SystemTenantContext.Instance, NullLogger<BudgetService>.Instance);
        teams.Items.Add(new Team(Guid.NewGuid(), Tenant, "Payments", ["ana", "svc-pay-*"], "test"));

        var tenantBudget = await service.UpsertAsync(null, BudgetScope.Tenant, null, 100m, "All AI", true, "Ana", CancellationToken.None);
        var teamBudget = await service.UpsertAsync(null, BudgetScope.Team, "Payments", 50m, null, true, "Ana", CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertAsync(null, BudgetScope.Team, "", 50m, null, true, "Ana", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertAsync(null, BudgetScope.Tenant, null, 0m, null, true, "Ana", CancellationToken.None));

        var monthStart = new DateOnly(Today.Year, Today.Month, 1);
        UsageFact F(string actor, DateOnly day, decimal cost) => new(Guid.NewGuid(), Tenant, actor, "claude-code", day, "m", 10, 0, 0, 0, 1, 1, cost, "v", "cli");
        facts.Items.Add(F("ana", Today, 45m));          // team 90 %, tenant 45 % (+ below)
        facts.Items.Add(F("svc-pay-1", Today, 0m));
        facts.Items.Add(F("carla", Today, 40m));        // tenant 85 %

        teams.Items[0].SetChannels(null, "https://hooks.slack.com/services/T/B/x", null);
        var raised = await service.EvaluateAsync(CancellationToken.None);
        Assert.Equal(4, raised); // tenant 50 + 80, team 50 + 80
        Assert.Equal(4, sink.Sent.Count);
        Assert.Contains(sink.Sent, m => m.Contains("Payments") && m.Contains("80%"));
        // Team alerts went to the team's channel; tenant alerts to the tenant's (null = tenant settings).
        Assert.Equal(2, sink.Channels.Count(c => c?.SlackWebhookUrl == "https://hooks.slack.com/services/T/B/x"));
        Assert.Equal(2, sink.Channels.Count(c => c is null));
        Assert.Throws<ArgumentException>(() => teams.Items[0].SetChannels("http://insecure.example", null, null));

        // Same state again → nothing new. Crossing 100 % on the team → exactly one more.
        Assert.Equal(0, await service.EvaluateAsync(CancellationToken.None));
        facts.Items.Add(F("ana", Today, 10m));
        Assert.Equal(1, await service.EvaluateAsync(CancellationToken.None));
        Assert.Contains(budgetsRepo.Alerts, a => a.Key == $"threshold:{teamBudget.Id}:{Today:yyyy-MM}:100");

        var status = await service.StatusAsync(CancellationToken.None);
        var team = status.Single(s => s.Budget.Id == teamBudget.Id);
        Assert.Equal(55m, team.SpentMonthToDate);
        Assert.Equal("over", team.State);
        Assert.Equal(95m, status.Single(s => s.Budget.Id == tenantBudget.Id).SpentMonthToDate);

        // Anomaly: seven quiet days at 2 USD, then 20 USD today (≥ 3× the median and ≥ 5 USD).
        var facts2 = new Facts();
        var repo2 = new Budgets();
        var sink2 = new Sink();
        var service2 = new BudgetService(repo2, new Teams(), facts2, sink2, new UnitOfWork(), SystemTenantContext.Instance, NullLogger<BudgetService>.Instance);
        for (var i = 1; i <= 7; i++)
        {
            facts2.Items.Add(F("ana", Today.AddDays(-i), 2m));
        }

        facts2.Items.Add(F("ana", Today, 20m));
        Assert.Equal(1, await service2.EvaluateAsync(CancellationToken.None));
        Assert.Equal("anomaly", repo2.Alerts.Single().Kind);
        Assert.Equal(0, await service2.EvaluateAsync(CancellationToken.None)); // once per day
    }

    [Fact]
    public void Teams_match_members_and_wildcards_and_roll_up_spend()
    {
        var team = new Team(Guid.NewGuid(), Tenant, "Data", ["Ana", "svc:etl-*"], "test");
        Assert.True(team.Matches("ana"));
        Assert.True(team.Matches("svc:etl-nightly"));
        Assert.False(team.Matches("bruno"));
        Assert.Equal(["Ana", "svc:etl-*"], team.Members);

        UsageFact F(string actor, decimal cost) => new(Guid.NewGuid(), Tenant, actor, "claude-code", Today, "m", 1000, 0, 0, 0, 1, 1, cost, "v", "cli");
        var rows = new List<UsageFact> { F("ana", 10m), F("svc:etl-nightly", 5m), F("bruno", 7m) };
        var byTeam = BudgetService.ByTeam([team], rows, rows, Today, new HashSet<string>(["ana"]));
        var data = byTeam.Single(t => t.Team == "Data");
        Assert.Equal(15m, data.EstimatedCost);
        Assert.Equal(2, data.Members);
        Assert.Equal(1, data.ActiveMembers);
        Assert.Equal(7m, byTeam.Single(t => t.Team == "(unassigned)").EstimatedCost);
    }

    [Fact]
    public void Provider_is_guessed_from_the_model_when_telemetry_does_not_say()
    {
        Assert.Equal("anthropic", ModelProviders.Guess("claude-opus-5"));
        Assert.Equal("openai", ModelProviders.Guess("gpt-5-mini"));
        Assert.Equal("openai", ModelProviders.Guess("o3"));
        Assert.Equal("google", ModelProviders.Guess("gemini-2.5-pro"));
        Assert.Null(ModelProviders.Guess("in-house-llm"));
        Assert.Equal("azure-openai", ModelProviders.Normalize("azure.ai.openai"));
        Assert.Equal("google", ModelProviders.Normalize("gcp.vertex_ai"));
    }
}
