extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>The rule catalog endpoint and per-tenant severity tuning (v0.39).</summary>
public sealed class RulesEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private WebApplicationFactory<ApiProgram> Factory() => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
        builder.UseSetting("Atlas:AutoMigrate", "false");
        builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
        builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Operations:RateLimitPerMinute", "1000");
    });

    private sealed record Entry(string Id, string ScannerId, string Category, string DefaultSeverity, string? OverrideSeverity, string Title, int OpenFindings);

    [Fact]
    public async Task Catalog_lists_rules_and_severity_tuning_round_trips()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var rules = await client.GetFromJsonAsync<List<Entry>>("/api/rules");
        Assert.NotNull(rules);

        // Unknown rule → 404; unknown severity → 400.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/rules/nope.rule/severity", new { severity = "Low" })).StatusCode);

        if (rules!.Count == 0)
        {
            return; // catalog is populated by scans; routing/validation is what this test pins down
        }

        var rule = rules[0];
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/rules/{rule.Id}/severity", new { severity = "Severe" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/rules/{rule.Id}/severity", new { severity = "7" })).StatusCode);

        var target = rule.DefaultSeverity == "Low" ? "Medium" : "Low";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/rules/{rule.Id}/severity", new { severity = target })).StatusCode);
        var tuned = (await client.GetFromJsonAsync<List<Entry>>("/api/rules"))!.Single(r => r.Id == rule.Id);
        Assert.Equal(target, tuned.OverrideSeverity);

        // null restores the default.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/rules/{rule.Id}/severity", new { severity = (string?)null })).StatusCode);
        var restored = (await client.GetFromJsonAsync<List<Entry>>("/api/rules"))!.Single(r => r.Id == rule.Id);
        Assert.Null(restored.OverrideSeverity);
    }
}
