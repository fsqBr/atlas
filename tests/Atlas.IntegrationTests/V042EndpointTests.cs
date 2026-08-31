extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>v0.42: demo estate seeding, compliance pack, issue-export validation.</summary>
public sealed class V042EndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    [Fact]
    public async Task Demo_estate_seeds_scores_everything_and_removes_cleanly()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var seeded = await client.PostAsync("/api/demo", null);
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
        var created = (await seeded.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("created").GetInt32();
        Assert.Equal(5, created);

        // Idempotent: a second seed creates nothing.
        var again = await client.PostAsync("/api/demo", null);
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("created").GetInt32());

        var rows = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/assessments");
        var demo = rows.EnumerateArray().Where(a => a.GetProperty("sourceLocator").GetString()!.StartsWith("demo://")).ToList();
        Assert.Equal(5, demo.Count);
        Assert.All(demo, a => Assert.True(a.GetProperty("healthScore").ValueKind == System.Text.Json.JsonValueKind.Number));

        // The demo estate feeds the trend and the modernization plan carries savings + payback.
        var trend = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/portfolio/trend?weeks=8");
        Assert.True(trend.GetArrayLength() >= 2);
        var withPlan = demo.First(a => a.GetProperty("name").GetString()!.Contains("Orion"));

        var removed = await client.DeleteAsync("/api/demo");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var afterRemove = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/assessments");
        Assert.DoesNotContain(afterRemove.EnumerateArray(), a => a.GetProperty("sourceLocator").GetString()!.StartsWith("demo://"));
    }

    [Fact]
    public async Task Compliance_pack_downloads_as_a_zip_and_issue_export_validates_the_credential()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/assessments", new { name = "Compliance", sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var zip = await client.GetAsync($"/api/assessments/{id}/compliance.zip");
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        Assert.Equal("application/zip", zip.Content.Headers.ContentType!.MediaType);
        using var archive = new System.IO.Compression.ZipArchive(await zip.Content.ReadAsStreamAsync());
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("summary.md", names);
        Assert.Contains("privacy-findings.csv", names);
        Assert.Contains("license-findings.csv", names);
        Assert.Contains("waivers.csv", names);

        // local source + no credential → clean 400, never a crash.
        var issues = await client.PostAsJsonAsync($"/api/assessments/{id}/export/issues", new { top = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, issues.StatusCode);
    }
}
