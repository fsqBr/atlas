using Atlas.Agent;
using Atlas.Application.Tenants;
using Atlas.Governance.Application;
using Atlas.Governance.Contracts;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Governance.Tests;

public class UsageReportTests
{
    private static readonly Guid Tenant = Atlas.Domain.Tenants.WellKnownTenants.DefaultId;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed class Facts : IUsageFactRepository
    {
        public List<UsageFact> Items { get; } = [];

        public Task UpsertAsync(IReadOnlyList<UsageFact> facts, CancellationToken ct)
        {
            foreach (var f in facts)
            {
                Items.RemoveAll(x => x.Actor == f.Actor && x.Tool == f.Tool && x.Period == f.Period && string.Equals(x.Model, f.Model, StringComparison.OrdinalIgnoreCase));
                Items.Add(f);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UsageFact>> ListAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UsageFact>>(Items.Where(i => i.Period >= from && i.Period <= to).ToList());

        public Task<UsageFact?> GetAsync(string actor, string tool, DateOnly period, string model, CancellationToken ct) =>
            Task.FromResult(Items.FirstOrDefault(i => i.Actor == actor && i.Tool == tool && i.Period == period && i.Model == model));

        public void Add(UsageFact fact) => Items.Add(fact);

        public Task<IReadOnlyList<UsageFact>> ListForActorAsync(string actor, string tool, DateOnly period, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UsageFact>>(Items.Where(i => i.Actor == actor && i.Tool == tool && i.Period == period).ToList());
    }

    private sealed class UnitOfWork : IGovernanceUnitOfWork
    {
        public int Saves { get; private set; }

        public Task SaveChangesAsync(CancellationToken ct)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedResolver(PriceCatalog catalog) : IPriceCatalogResolver
    {
        public Task<PriceCatalog> ResolveAsync(CancellationToken ct) => Task.FromResult(catalog);
    }

    private static UsageReportService Service(Facts facts, UnitOfWork? uow = null) =>
        new(facts, new FixedResolver(PriceCatalog.LoadBuiltin()), uow ?? new UnitOfWork(), SystemTenantContext.Instance, NullLogger<UsageReportService>.Instance);

    [Fact]
    public void Builtin_price_catalog_loads_and_prices_known_models_only()
    {
        var prices = PriceCatalog.LoadBuiltin();
        Assert.False(string.IsNullOrWhiteSpace(prices.Version));
        Assert.Equal("USD", prices.Currency);

        // 1M input + 1M output on Opus 4 at list price: 15 + 75.
        Assert.Equal(90m, prices.Estimate("claude-opus-4-1-20250805", 1_000_000, 1_000_000, 0, 0));
        // Cache tokens use the cache prices when present (Sonnet: 0.3 read / 3.75 write per 1M).
        Assert.Equal(4.05m, prices.Estimate("claude-sonnet-4-20250514", 0, 0, 1_000_000, 1_000_000));
        // A model without cache prices falls back to the input price for cache tokens.
        Assert.Equal(2.0m, prices.Estimate("mistral-large-latest", 0, 0, 1_000_000, 0));
        // Unknown model → null, never zero (unpriced is visible, not hidden in a total).
        Assert.Null(prices.Estimate("some-internal-model", 5, 5, 5, 5));
        // More specific patterns must come before their prefix (gpt-4o-mini before gpt-4o, gpt-5-mini before gpt-5).
        Assert.Equal(0.15m, prices.Find("gpt-4o-mini-2024-07-18")!.Input);
        Assert.Equal(0.25m, prices.Find("gpt-5-mini")!.Input);
        Assert.Equal(1.25m, prices.Find("gpt-5")!.Input);
    }

    [Fact]
    public void Custom_price_catalog_is_validated()
    {
        static PriceCatalog Parse(string json)
        {
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            return PriceCatalog.Load(ms, "test");
        }

        Assert.Throws<InvalidOperationException>(() => Parse("""{"version":"x","models":[]}"""));
        Assert.Throws<InvalidOperationException>(() => Parse("""{"version":"x","models":[{"pattern":"^a","input":-1,"output":1}]}"""));
        Assert.ThrowsAny<ArgumentException>(() => Parse("""{"version":"x","models":[{"pattern":"^(a","input":1,"output":1}]}"""));
        var ok = Parse("""{"version":"corp-1","models":[{"pattern":"^a","input":1,"output":1}]}""");
        Assert.Equal("corp-1", ok.Version);
        Assert.Equal("USD", ok.Currency);
    }

    [Fact]
    public async Task Ingest_estimates_flags_unpriced_merges_duplicates_and_is_idempotent()
    {
        var facts = new Facts();
        var uow = new UnitOfWork();
        var service = Service(facts, uow);
        var day = Today.AddDays(-1);

        var result = await service.IngestAsync("felipe", "Claude-Code", "cli",
        [
            new UsageEntry(day, "claude-opus-4-1", 1_000_000, 0, 0, 0, 10, 2),
            new UsageEntry(day, "claude-opus-4-1", 0, 1_000_000, 0, 0, 5, 2), // same key in one report → merged
            new UsageEntry(day, "my-private-model", 100, 100, 0, 0, 1, 1),   // unpriced
            new UsageEntry(day.AddDays(-500), "claude-opus-4-1", 1, 1, 0, 0, 1, 1), // too old → rejected
            new UsageEntry(day, "claude-opus-4-1", -1, 0, 0, 0, 1, 1),      // negative → rejected
        ], CancellationToken.None);

        Assert.Equal(2, result.Accepted);
        Assert.Equal(2, result.Rejected);
        Assert.Equal(1, result.UnpricedEntries);
        Assert.Equal(90m, result.EstimatedCost);
        Assert.Equal(1, uow.Saves);
        Assert.Equal(2, facts.Items.Count);
        var opus = facts.Items.Single(f => f.Model == "claude-opus-4-1");
        Assert.Equal("claude-code", opus.Tool); // normalised
        Assert.Equal(15, opus.Requests);
        Assert.Equal(2, opus.Sessions); // max, not sum: the same two sessions
        Assert.Equal(90m, opus.EstimatedCost);
        Assert.Null(facts.Items.Single(f => f.Model == "my-private-model").EstimatedCost);

        // Re-running the CLI for the same day replaces the line instead of adding to it.
        await service.IngestAsync("felipe", "claude-code", "cli", [new UsageEntry(day, "claude-opus-4-1", 2_000_000, 0, 0, 0, 20, 3)], CancellationToken.None);
        Assert.Equal(2, facts.Items.Count);
        Assert.Equal(30m, facts.Items.Single(f => f.Model == "claude-opus-4-1").EstimatedCost);

        var summary = await service.SummaryAsync(30, CancellationToken.None);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.ReportingActors);
        Assert.Equal(30m, summary.EstimatedCost);
        Assert.Equal(200, summary.UnpricedTokens);
        var actor = Assert.Single(summary.Actors);
        Assert.Equal("felipe", actor.Actor);
        Assert.Equal(["claude-code"], actor.Tools);
        Assert.Equal(3, actor.Sessions);
        Assert.Equal(21, actor.Requests);
        Assert.Equal(2, summary.ByModel.Count);
        Assert.Null(summary.ByModel.Single(m => m.Model == "my-private-model").EstimatedCost);
        Assert.Single(summary.ByDay);
    }

    [Fact]
    public async Task Ingest_requires_actor_and_tool_and_caps_size()
    {
        var service = Service(new Facts());
        await Assert.ThrowsAsync<ArgumentException>(() => service.IngestAsync(" ", "claude-code", "cli", [], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.IngestAsync("me", "", "cli", [], CancellationToken.None));
        var tooMany = Enumerable.Range(0, UsageReportService.MaxEntriesPerReport + 1).Select(i => new UsageEntry(Today, "m" + i, 1, 1, 0, 0, 1, 1)).ToList();
        await Assert.ThrowsAsync<ArgumentException>(() => service.IngestAsync("me", "claude-code", "cli", tooMany, CancellationToken.None));
        Assert.Null(await service.SummaryAsync(30, CancellationToken.None));
    }

    [Fact]
    public void Summary_ranks_actors_by_estimated_cost_and_counts_sessions_per_day()
    {
        var prices = PriceCatalog.LoadBuiltin();
        var d1 = Today.AddDays(-2);
        var d2 = Today.AddDays(-1);
        UsageFact F(string actor, DateOnly day, string model, long input, int sessions) =>
            new(Guid.NewGuid(), Tenant, actor, "claude-code", day, model, input, 0, 0, 0, 1, sessions, prices.Estimate(model, input, 0, 0, 0), prices.Version, "cli");

        var summary = UsageReportService.Build(30, Today.AddDays(-30), Today,
        [
            F("ana", d1, "claude-sonnet-4", 1_000_000, 2), F("ana", d2, "claude-sonnet-4", 1_000_000, 1),
            F("bruno", d1, "claude-opus-4-1", 1_000_000, 1),
        ], prices);

        Assert.Equal(2, summary.ReportingActors);
        Assert.Equal("bruno", summary.Actors[0].Actor); // 15 > 6
        Assert.Equal(15m, summary.Actors[0].EstimatedCost);
        Assert.Equal(6m, summary.Actors[1].EstimatedCost);
        Assert.Equal(3, summary.Actors[1].Sessions);
        Assert.Equal(2, summary.ByDay.Count);
        Assert.Equal(2, summary.ByDay[0].Actors);
        Assert.Equal(21m, summary.EstimatedCost);
    }

    [Fact]
    public void Claude_code_reader_aggregates_by_day_and_model_and_dedupes_streamed_chunks()
    {
        var day = Today.AddDays(-1).ToString("yyyy-MM-dd");
        var old = Today.AddDays(-40).ToString("yyyy-MM-dd");
        static string Line(string type, string model, string id, string req, string session, string ts, long input, long output, long cr = 0, long cw = 0) =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type,
                timestamp = ts + "T10:00:00.000Z",
                sessionId = session,
                requestId = req,
                cwd = "C:\\secret\\path",
                message = new { id, model, usage = new { input_tokens = input, output_tokens = output, cache_read_input_tokens = cr, cache_creation_input_tokens = cw } },
            });

        var lines = new[]
        {
            Line("assistant", "claude-opus-5", "msg_1", "req_1", "s1", day, 100, 10, 1000, 50),
            Line("assistant", "claude-opus-5", "msg_1", "req_1", "s1", day, 100, 20, 1000, 50), // streamed chunk: same message → counted once
            Line("assistant", "claude-opus-5", "msg_2", "req_2", "s2", day, 5, 5),
            Line("assistant", "claude-haiku-4-5", "msg_3", "req_3", "s1", day, 7, 7),
            Line("assistant", "claude-opus-5", "msg_4", "req_4", "s1", old, 999, 999),           // outside window
            Line("user", "claude-opus-5", "msg_5", "req_5", "s1", day, 999, 999),                // not an assistant line
            Line("assistant", "<synthetic>", "msg_6", "req_6", "s1", day, 999, 999),             // synthetic placeholder
            "not json at all",
            "{\"type\":\"assistant\",\"timestamp\":\"" + day + "T10:00:00Z\",\"message\":{\"model\":\"x\"}}", // no usage
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var agg = new Dictionary<(DateOnly, string), ClaudeCodeUsageReader.Acc>();
        ClaudeCodeUsageReader.ReadLines(lines, Today.AddDays(-7), Today, seen, agg);

        Assert.Equal(2, agg.Count);
        var opus = agg[(Today.AddDays(-1), "claude-opus-5")];
        Assert.Equal(105, opus.Input);
        Assert.Equal(15, opus.Output);
        Assert.Equal(1000, opus.CacheRead);
        Assert.Equal(50, opus.CacheWrite);
        Assert.Equal(2, opus.Requests);
        Assert.Equal(2, opus.Sessions.Count);
        var haiku = agg[(Today.AddDays(-1), "claude-haiku-4-5")];
        Assert.Equal(1, haiku.Requests);
    }

    [Fact]
    public void Claude_code_reader_reads_folders_and_emits_contract_entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-agent-test-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "C--some-project");
        Directory.CreateDirectory(project);
        try
        {
            var day = Today.ToString("yyyy-MM-dd");
            static string Row(string day, string hour, string id, long input, long output) =>
                System.Text.Json.JsonSerializer.Serialize(new { type = "assistant", timestamp = $"{day}T{hour}:00:00Z", sessionId = "s", requestId = "r-" + id, message = new { id, model = "claude-sonnet-4-5", usage = new { input_tokens = input, output_tokens = output } } });
            File.WriteAllLines(Path.Combine(project, "session.jsonl"), [Row(day, "01", "m1", 10, 20), Row(day, "02", "m2", 1, 2)]);

            IReadOnlyList<UsageEntryRequest> entries = ClaudeCodeUsageReader.Read([root, Path.Combine(root, "does-not-exist")], Today.AddDays(-1), Today, out var files);
            Assert.Equal(1, files);
            var e = Assert.Single(entries);
            Assert.Equal(Today, e.Period);
            Assert.Equal("claude-sonnet-4-5", e.Model);
            Assert.Equal(11, e.InputTokens);
            Assert.Equal(22, e.OutputTokens);
            Assert.Equal(2, e.Requests);
            Assert.Equal(1, e.Sessions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
