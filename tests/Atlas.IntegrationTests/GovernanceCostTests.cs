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

        var usage = await client.GetFromJsonAsync<UsageSummaryRow>("/api/ai-estate/usage?days=30");
        Assert.Equal(1, usage!.ReportingActors);
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
    }

    private sealed record PriceRow(Guid? Id, string Pattern, string Source);

    private sealed record UnpricedRow(string Model, long Tokens);

    private sealed record UsageReportRow(int Accepted, int Rejected, decimal EstimatedCost, int UnpricedEntries);

    private sealed record UsageActorRow(string Actor, long Tokens, int Requests, decimal EstimatedCost);

    private sealed record UsageSummaryRow(int ReportingActors, decimal EstimatedCost, long UnpricedTokens, string PriceCatalogVersion, List<UsageActorRow> Actors);

    private sealed record PriceCatalogRow(string Version, List<PriceRow> Prices, List<UnpricedRow> Unpriced);
}
