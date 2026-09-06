extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>The two endpoints the CI scripts rely on: repository lookup and the quality gate.</summary>
public sealed class CiGateTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private WebApplicationFactory<ApiProgram> Factory() => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
        builder.UseSetting("Atlas:AutoMigrate", "false");
        builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
        builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Operations:RateLimitPerMinute", "1000");
        builder.UseSetting("Atlas:Connectors:Git:AllowedHosts", "example.test");
    });

    private sealed record Created(Guid Id, Guid JobId);
    private sealed record Found(Guid Id, string Name, string SourceLocator, string? Branch);
    private sealed record Gate(bool Passed, bool Evaluated, int? Score, Dictionary<string, int> OpenBySeverity, List<string> Violations, string? FailOn, int? MinScore, string? ReportUrl);

    [Fact]
    public async Task By_locator_normalizes_the_repository_url_and_the_gate_fails_closed_before_a_run_completes()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var repo = $"https://example.test/acme/{Guid.NewGuid():N}.git";
        var created = await (await client.PostAsJsonAsync("/api/assessments", new { name = "Acme CI", sourceKind = "git", sourceLocator = repo, branch = "main" })).Content.ReadFromJsonAsync<Created>();

        var same = await client.GetFromJsonAsync<Found>($"/api/assessments/by-locator?locator={Uri.EscapeDataString(repo)}&kind=git&branch=main");
        Assert.Equal(created!.Id, same!.Id);

        var variant = await client.GetFromJsonAsync<Found>($"/api/assessments/by-locator?locator={Uri.EscapeDataString(repo.Replace(".git", "/").ToUpperInvariant())}&kind=git");
        Assert.Equal(created.Id, variant!.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/assessments/by-locator?locator=https://example.test/nobody/nothing.git")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/assessments/by-locator?locator=")).StatusCode);

        var gate = await client.GetFromJsonAsync<Gate>($"/api/assessments/{created.Id}/gate?failOn=High&minScore=60");
        Assert.False(gate!.Passed);
        Assert.False(gate.Evaluated);
        Assert.Contains("No completed run to evaluate.", gate.Violations);
        Assert.Equal("High", gate.FailOn);
        Assert.Equal(60, gate.MinScore);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/assessments/{created.Id}/gate?failOn=Severe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/assessments/{Guid.NewGuid()}/gate")).StatusCode);
    }
}
