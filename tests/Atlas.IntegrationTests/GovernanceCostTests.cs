extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Atlas.Application.AiEstate;
using Atlas.Application.Credentials;
using Atlas.Application.Portfolio;
using Atlas.Governance.Application;
using Atlas.Governance.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>
/// Governance module on real Postgres: its own schema and migrations, cost sources referencing
/// stored credentials, an idempotent sync through a fake provider client, and the summary reaching the
/// portfolio. The API surface is exercised for validation and routing; no test ever calls a provider.
/// </summary>
public sealed class GovernanceCostTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FakeOpenAi : ICostProviderClient
    {
        public string Provider => CostProviders.OpenAi;

        public int Calls { get; private set; }

        public Task<CostCollection> CollectAsync(CostSource source, string secret, DateOnly from, DateOnly to, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("sk-admin-test-key-0123456789", secret);
            return Task.FromResult(new CostCollection(
            [
                new CostFact(Guid.NewGuid(), source.TenantId, source.Id, Provider, CostBasis.ProviderReported, to, "project", "proj_billing", "GPT-4o", 12.5m, "USD", null, null, "test"),
                new CostFact(Guid.NewGuid(), source.TenantId, source.Id, Provider, CostBasis.ProviderReported, to.AddDays(-1), "project", "proj_billing", "Embeddings", 0.5m, "USD", null, null, "test"),
            ], []));
        }
    }

    private WebApplicationFactory<ApiProgram> Factory() => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
        builder.UseSetting("Atlas:AutoMigrate", "false");
        builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
        builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        builder.UseSetting("Atlas:Operations:RateLimitPerMinute", "1000");
    });

    private sealed record SourceRow(string Provider, string CredentialName, string? Scope, bool Enabled, string? LastSyncStatus);

    private sealed record AllowlistRow(List<string> ApprovedProviders, string Source, string? UpdatedBy);

    private sealed record CatalogProviderRow(string Id, string Name, string Kind);

    [Fact]
    public async Task Cost_source_sync_and_summary_flow_through_the_governance_schema()
    {
        var fake = new FakeOpenAi();
        await using var provider = fixture.BuildServices(services =>
        {
            services.RemoveAll<ICostProviderClient>();
            services.AddSingleton<ICostProviderClient>(fake);
        });

        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CredentialsService>().UpsertAsync("openai-admin", null, "sk-admin-test-key-0123456789", "OpenAI org admin key", CancellationToken.None);
            var sources = scope.ServiceProvider.GetRequiredService<CostSourceService>();
            await Assert.ThrowsAsync<ArgumentException>(() => sources.UpsertAsync("openai", "does-not-exist", null, true, CancellationToken.None));
            var created = await sources.UpsertAsync("openai", "openai-admin", null, true, CancellationToken.None);
            Assert.Null(created.LastSyncAtUtc);
        }

        using (var scope = provider.CreateScope())
        {
            var results = await scope.ServiceProvider.GetRequiredService<CostSyncService>().SyncAsync(30, CancellationToken.None);
            var result = Assert.Single(results);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(2, result.Facts);
            Assert.Equal(1, fake.Calls);
        }

        using (var scope = provider.CreateScope())
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var facts = await scope.ServiceProvider.GetRequiredService<ICostFactRepository>().ListAsync(today.AddDays(-30), today, CancellationToken.None);
            Assert.Equal(2, facts.Count);
            Assert.All(facts, f => Assert.Equal(CostBasis.ProviderReported, f.Basis));

            var source = Assert.Single(await scope.ServiceProvider.GetRequiredService<ICostSourceRepository>().ListAsync(CancellationToken.None));
            Assert.Equal("Succeeded", source.LastSyncStatus);
            Assert.NotNull(source.LastSyncAtUtc);

            // The core's port is now served by the module: spend reaches the portfolio without the core knowing the module.
            var summary = await scope.ServiceProvider.GetRequiredService<IAiCostSummarySource>().GetAsync(30, CancellationToken.None);
            Assert.NotNull(summary);
            var openai = Assert.Single(summary.Providers);
            Assert.Equal(13.0m, openai.Total);
            Assert.Equal("ProviderReported", openai.Basis);

            var portfolio = await scope.ServiceProvider.GetRequiredService<PortfolioBuilder>().BuildAsync(null, CancellationToken.None);
            Assert.NotNull(portfolio.AiEstate);
            Assert.NotNull(portfolio.AiEstate.Costs);
            Assert.Equal(13.0m, portfolio.AiEstate.Costs.Providers.Single().Total);
        }

        // Idempotent: a second sync converges to the same two rows.
        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CostSyncService>().SyncAsync(30, CancellationToken.None);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            Assert.Equal(2, (await scope.ServiceProvider.GetRequiredService<ICostFactRepository>().ListAsync(today.AddDays(-30), today, CancellationToken.None)).Count);
        }

        // API surface: routing, validation and the key-never-travels contract.
        using var factory = Factory();
        using var client = factory.CreateClient();
        var listed = await client.GetFromJsonAsync<List<SourceRow>>("/api/ai-estate/cost/sources");
        var row = Assert.Single(listed!);
        Assert.Equal("openai", row.Provider);
        Assert.Equal("Succeeded", row.LastSyncStatus);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/cost/sources/anthropic", new { credentialName = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/cost/sources/github-copilot", new { credentialName = "openai-admin" })).StatusCode); // scope required
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/cost/sources/bedrock", new { credentialName = "openai-admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/ai-estate/cost?days=30")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/ai-estate/cost/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/ai-estate/cost/sources/openai")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/ai-estate/cost/sources/openai")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/ai-estate/cost")).StatusCode); // no sources left → nothing to summarize

        // Allowlist: saved per tenant in the UI, validated against the catalog, removable.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/allowlist", new { approvedProviders = new[] { "not-a-provider" } })).StatusCode);
        var saved = await client.PutAsJsonAsync("/api/ai-estate/allowlist", new { approvedProviders = new[] { "Azure-OpenAI", "anthropic", "anthropic" }, author = "Ana" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var allowlist = await client.GetFromJsonAsync<AllowlistRow>("/api/ai-estate/allowlist");
        Assert.Equal(["anthropic", "azure-openai"], allowlist!.ApprovedProviders);
        Assert.Equal("tenant", allowlist.Source);
        Assert.Equal("Ana", allowlist.UpdatedBy);
        var providers = await client.GetFromJsonAsync<List<CatalogProviderRow>>("/api/ai-estate/catalog/providers");
        Assert.Contains(providers!, p => p.Id == "azure-openai" && p.Kind == "external-api");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/ai-estate/allowlist")).StatusCode);
        Assert.Equal("none", (await client.GetFromJsonAsync<AllowlistRow>("/api/ai-estate/allowlist"))!.Source);

        // Admin role guard covers the write side of the cost routes and the allowlist.
        var options = new AtlasApi::Atlas.Api.AuthOptions();
        Assert.Equal(options.AdminRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "PUT", "/api/ai-estate/allowlist"));
        Assert.Equal(options.AdminRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "PUT", "/api/ai-estate/cost/sources/openai"));
        Assert.Equal(options.AdminRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "POST", "/api/ai-estate/cost/sync"));
        Assert.Equal(options.AnalystRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "GET", "/api/ai-estate/cost"));
        // …except the developer usage report: analysts (developers with a PAT) post their own usage; reads stay analyst too.
        Assert.Equal(options.AnalystRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "POST", "/api/ai-estate/usage/report"));
        Assert.Equal(options.AnalystRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "GET", "/api/ai-estate/usage"));

        // Developer usage: the CLI's report round-trips into the per-actor summary; re-posting is idempotent.
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/ai-estate/usage")).StatusCode);
        var day0 = DateOnly.FromDateTime(DateTime.UtcNow);
        var report = new
        {
            actor = "dev-1",
            tool = "claude-code",
            agentVersion = "0.53.0",
            entries = new[]
            {
                new { period = day0.AddDays(-1), model = "claude-opus-4-1", inputTokens = 1_000_000L, outputTokens = 0L, cacheReadTokens = 0L, cacheWriteTokens = 0L, requests = 3, sessions = 1 },
                new { period = day0, model = "in-house-llm", inputTokens = 10L, outputTokens = 10L, cacheReadTokens = 0L, cacheWriteTokens = 0L, requests = 1, sessions = 1 },
            },
        };
        var posted = await client.PostAsJsonAsync("/api/ai-estate/usage/report", report);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var accepted = await posted.Content.ReadFromJsonAsync<UsageReportRow>();
        Assert.Equal(2, accepted!.Accepted);
        Assert.Equal(1, accepted.UnpricedEntries);
        Assert.Equal(15m, accepted.EstimatedCost);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/ai-estate/usage/report", report)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/ai-estate/usage/report", new { actor = "", tool = "claude-code", entries = Array.Empty<object>() })).StatusCode);

        // Live view: the report left an event for today; the actor shows as active with today's running totals; forecast present.
        var live = await client.GetFromJsonAsync<LiveRow>("/api/ai-estate/usage/live?activeMinutes=15");
        Assert.Equal(1, live!.ActiveActors);
        Assert.Equal(20, live.TokensToday); // only the in-house-llm line is dated today
        var liveActor = Assert.Single(live.Actors);
        Assert.Equal("dev-1", liveActor.Actor);
        Assert.True(liveActor.ActiveNow);
        Assert.NotEmpty(live.Curve);

        var usage = await client.GetFromJsonAsync<UsageSummaryRow>("/api/ai-estate/usage?days=30");
        Assert.Equal(1, usage!.ReportingActors);
        Assert.True(usage.Forecast.MonthToDateCost >= 0);
        Assert.Equal(DateTime.UtcNow.Day, usage.Forecast.DaysElapsedInMonth);
        Assert.Equal(15m, usage.EstimatedCost);
        Assert.Equal(20, usage.UnpricedTokens);
        var dev = Assert.Single(usage.Actors);
        Assert.Equal("dev-1", dev.Actor);
        Assert.Equal(1_000_020, dev.Tokens);
        Assert.Equal(4, dev.Requests);
        var prices = await client.GetFromJsonAsync<PriceCatalogRow>("/api/ai-estate/usage/prices");
        Assert.Equal(usage.PriceCatalogVersion, prices!.Version);
        Assert.NotEmpty(prices.Prices);
        Assert.Contains(prices.Unpriced, u => u.Model == "in-house-llm");

        // Tenant price for the in-house model: saved, listed first as "tenant", and the stored estimate is repriced.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/usage/prices", new { pattern = "^(", input = 1, output = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/usage/prices", new { pattern = "in-house-llm", input = -1, output = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/usage/prices", new { pattern = "in-house-llm", input = 100000, output = 1 })).StatusCode); // per-million unit guard
        var savedPrice = await client.PutAsJsonAsync("/api/ai-estate/usage/prices", new { pattern = "^in-house-llm$", input = 1000m, output = 1000m, author = "Ana" });
        Assert.Equal(HttpStatusCode.OK, savedPrice.StatusCode);
        var priceRow = await savedPrice.Content.ReadFromJsonAsync<PriceRow>();
        Assert.Equal("tenant", priceRow!.Source);
        prices = await client.GetFromJsonAsync<PriceCatalogRow>("/api/ai-estate/usage/prices");
        Assert.EndsWith("+tenant", prices!.Version);
        Assert.Equal("tenant", prices.Prices[0].Source);
        Assert.DoesNotContain(prices.Unpriced, u => u.Model == "in-house-llm");
        usage = await client.GetFromJsonAsync<UsageSummaryRow>("/api/ai-estate/usage?days=30");
        Assert.Equal(15.02m, usage!.EstimatedCost); // 15 + (10+10 tokens × 1000/1M = 0.02)
        Assert.Equal(0, usage.UnpricedTokens);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/ai-estate/usage/prices/{priceRow.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/ai-estate/usage/prices/{priceRow.Id}")).StatusCode);
        usage = await client.GetFromJsonAsync<UsageSummaryRow>("/api/ai-estate/usage?days=30");
        Assert.Equal(15m, usage!.EstimatedCost);
        Assert.Equal(20, usage.UnpricedTokens);

        // OpenTelemetry receiver: a Claude Code-shaped delta export lands as an otel fact, pseudonymised, and shows live.
        var nanos = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L).ToString();
        const string otlp = """{"resourceMetrics":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"claude-code"}}]},"scopeMetrics":[{"metrics":[{"name":"claude_code.token.usage","sum":{"aggregationTemporality":1,"isMonotonic":true,"dataPoints":[{"attributes":[{"key":"type","value":{"stringValue":"input"}},{"key":"model","value":{"stringValue":"claude-sonnet-4-5"}},{"key":"user.email","value":{"stringValue":"ana@example.com"}}],"timeUnixNano":"@now@","asInt":"1000000"}]}}]}]}]}""";
        var otlpResponse = await client.PostAsync("/api/ai-estate/otlp/v1/metrics", new StringContent(otlp.Replace("@now@", nanos), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, otlpResponse.StatusCode);
        var proto = await client.PostAsync("/api/ai-estate/otlp/v1/metrics", new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf") } });
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, proto.StatusCode);
        Assert.Equal(options.AnalystRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "POST", "/api/ai-estate/otlp/v1/metrics"));
        usage = await client.GetFromJsonAsync<UsageSummaryRow>("/api/ai-estate/usage?days=30");
        var otelActor = Assert.Single(usage!.Actors, a => a.Actor.StartsWith("otel-", StringComparison.Ordinal));
        Assert.DoesNotContain("example.com", otelActor.Actor);
        Assert.Equal(18m, usage.EstimatedCost); // 15 + 1M Sonnet input tokens at 3.00/M
        Assert.Contains(usage.ByProvider, p => p.Provider == "anthropic");
        Assert.Contains(usage.ByTool, t => t.Tool == "claude-code" && t.Source.Contains("otel"));
        live = await client.GetFromJsonAsync<LiveRow>("/api/ai-estate/usage/live?activeMinutes=15");
        Assert.Contains(live!.Actors, a => a.Actor == otelActor.Actor && a.ActiveNow);

        // Teams and budgets: a team over budget raises threshold alerts once; the summary rolls up per team.
        var team = await client.PutAsJsonAsync("/api/ai-estate/teams", new { name = "Platform", members = new[] { "dev-1", otelActor.Actor } });
        Assert.Equal(HttpStatusCode.OK, team.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/teams", new { name = "Platform", members = new[] { "x" } })).StatusCode); // duplicate name
        var actorsKnown = await client.GetFromJsonAsync<List<KnownActorRow>>("/api/ai-estate/teams/actors");
        Assert.Contains(actorsKnown!, a => a.Actor == "dev-1" && a.Team == "Platform");
        var budget = await client.PutAsJsonAsync("/api/ai-estate/budgets", new { scope = "team", scopeKey = "Platform", monthlyAmount = 10, name = "Platform AI" });
        Assert.Equal(HttpStatusCode.OK, budget.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/budgets", new { scope = "galaxy", monthlyAmount = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/ai-estate/budgets", new { scope = "tenant", monthlyAmount = 0 })).StatusCode);
        // Alerts are evaluated on ingest: one more report triggers 50/80/100 for the 18 USD spent against 10.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/ai-estate/usage/report", report)).StatusCode);
        var statuses = await client.GetFromJsonAsync<List<BudgetRow>>("/api/ai-estate/budgets");
        var platform = Assert.Single(statuses!);
        Assert.Equal("over", platform.State);
        Assert.Equal(18m, platform.SpentMonthToDate);
        var alerts = await client.GetFromJsonAsync<List<AlertRow>>("/api/ai-estate/budgets/alerts?days=1");
        Assert.Equal(3, alerts!.Count(a => a.Kind == "threshold"));
        Assert.All(alerts, a => Assert.False(a.Delivered)); // no channel configured in the test tenant
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/ai-estate/usage/report", report)).StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<List<AlertRow>>("/api/ai-estate/budgets/alerts?days=1"))!.Count); // once per month
        usage = await client.GetFromJsonAsync<UsageSummaryRow>("/api/ai-estate/usage?days=30");
        Assert.Contains(usage!.ByTeam, x => x.Team == "Platform" && x.EstimatedCost == 18m);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/ai-estate/budgets/{platform.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/ai-estate/teams/{(await team.Content.ReadFromJsonAsync<TeamRow>())!.Id}")).StatusCode);
    }

    private sealed record KnownActorRow(string Actor, string? Team);

    private sealed record TeamRow(Guid Id, string Name);

    private sealed record BudgetRow(Guid Id, string State, decimal SpentMonthToDate);

    private sealed record AlertRow(string Kind, bool Delivered);

    private sealed record PriceRow(Guid? Id, string Pattern, string Source);

    private sealed record UnpricedRow(string Model, long Tokens);

    private sealed record UsageReportRow(int Accepted, int Rejected, decimal EstimatedCost, int UnpricedEntries);

    private sealed record UsageActorRow(string Actor, long Tokens, int Requests, decimal EstimatedCost);

    private sealed record ForecastRow(decimal MonthToDateCost, int DaysElapsedInMonth, decimal ProjectedMonthCost);

    private sealed record ProviderRow(string Provider);

    private sealed record ToolRow(string Tool, string Source);

    private sealed record TeamSpendRow(string Team, decimal EstimatedCost);

    private sealed record UsageSummaryRow(int ReportingActors, decimal EstimatedCost, long UnpricedTokens, string PriceCatalogVersion, List<UsageActorRow> Actors, ForecastRow Forecast, List<ProviderRow> ByProvider, List<ToolRow> ByTool, List<TeamSpendRow> ByTeam);

    private sealed record LiveActorRow(string Actor, bool ActiveNow, long TokensToday);

    private sealed record LiveRow(int ActiveActors, long TokensToday, List<LiveActorRow> Actors, List<object> Curve);

    private sealed record PriceCatalogRow(string Version, List<PriceRow> Prices, List<UnpricedRow> Unpriced);
}
