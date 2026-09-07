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

        // Admin role guard covers the write side of the cost routes.
        var options = new AtlasApi::Atlas.Api.AuthOptions();
        Assert.Equal(options.AdminRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "PUT", "/api/ai-estate/cost/sources/openai"));
        Assert.Equal(options.AdminRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "POST", "/api/ai-estate/cost/sync"));
        Assert.Equal(options.AnalystRole, AtlasApi::Atlas.Api.AuthSetup.RequiredRole(options, "GET", "/api/ai-estate/cost"));
    }
}
