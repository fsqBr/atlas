extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>v0.45: portfolio executive report, baseline gate (?failOnNew=), dead-member rule in the catalog.</summary>
public sealed class V045EndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed record Created(Guid Id, Guid JobId);

    private WebApplicationFactory<ApiProgram> Factory() => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
        builder.UseSetting("Atlas:AutoMigrate", "false");
        builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
        builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Operations:RateLimitPerMinute", "1000");
    });

    [Fact]
    public async Task Gate_baseline_knob_is_validated_and_reported()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/assessments", new { name = "Baseline gate", sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })).Content.ReadFromJsonAsync<Created>();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/assessments/{created!.Id}/gate?failOnNew=Severe")).StatusCode);

        var gate = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/assessments/{created.Id}/gate?failOnNew=High");
        Assert.Equal("High", gate.GetProperty("failOnNew").GetString());
        Assert.False(gate.GetProperty("passed").GetBoolean()); // no completed run: the gate fails closed
        Assert.False(gate.GetProperty("evaluated").GetBoolean());

        // Without the knob the response stays backward-compatible (failOnNew null).
        var plain = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/assessments/{created.Id}/gate");
        Assert.True(!plain.TryGetProperty("failOnNew", out var fon) || fon.ValueKind == System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Portfolio_report_renders_html_scoped_by_tag_and_localized()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/assessments", new { name = "Estate Alpha Report", sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })).Content.ReadFromJsonAsync<Created>();
        var tagged = await client.PutAsJsonAsync($"/api/assessments/{created!.Id}/tags", new { tags = new[] { "grupo-x" } });
        Assert.Equal(HttpStatusCode.OK, tagged.StatusCode);

        var html = await client.GetStringAsync("/api/portfolio/report?lang=en");
        Assert.Contains("Portfolio Report", html);
        Assert.Contains("Estate Alpha Report", html);

        // Accented strings are HTML-encoded by the renderer, so assert on ASCII anchors.
        var pt = await client.GetStringAsync("/api/portfolio/report?lang=pt-BR&tag=grupo-x");
        Assert.Contains("lang=\"pt-BR\"", pt);
        Assert.Contains("Grupo de produto", pt);
        Assert.Contains("grupo-x", pt);
        Assert.Contains("Estate Alpha Report", pt);

        // A tag that matches nothing has no report.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/portfolio/report?tag=nao-existe")).StatusCode);
    }
}
