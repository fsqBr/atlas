extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

public sealed class CompareTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    private sealed record Created(Guid Id, Guid JobId);

    [Fact]
    public async Task Compares_two_assessments_and_rejects_bad_pairs()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var a = await (await client.PostAsJsonAsync("/api/assessments", new { name = "Left", sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })).Content.ReadFromJsonAsync<Created>();
        var b = await (await client.PostAsJsonAsync("/api/assessments", new { name = "Right", sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })).Content.ReadFromJsonAsync<Created>();

        var response = await client.GetAsync($"/api/assessments/compare?a={a!.Id}&b={b!.Id}&lang=pt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Left", json.GetProperty("a").GetProperty("name").GetString());
        Assert.Equal("Right", json.GetProperty("b").GetProperty("name").GetString());
        Assert.Equal(0, json.GetProperty("a").GetProperty("openFindings").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("a").GetProperty("score").ValueKind);
        Assert.Equal(0, json.GetProperty("ruleDifferences").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/assessments/compare?a={a.Id}&b={a.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/assessments/compare?a={a.Id}&b={Guid.NewGuid()}")).StatusCode);
    }
}
