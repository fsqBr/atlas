extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>v0.40: tenant cost profile, assessment tags, waiver expiry on policies, SARIF import.</summary>
public sealed class V040EndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    private static Task<Created?> CreateAssessmentAsync(HttpClient client, string name) =>
        client.PostAsJsonAsync("/api/assessments", new { name, sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<Created>()).Unwrap();

    [Fact]
    public async Task Cost_profile_round_trips_and_resets()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var defaults = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/settings/cost");
        Assert.True(defaults.GetProperty("isDefault").GetBoolean());

        var put = await client.PutAsJsonAsync("/api/settings/cost", new { currency = "usd", hourlyRate = 120, teamSize = 6, author = "cfo" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/settings/cost");
        Assert.Equal("USD", saved.GetProperty("currency").GetString());
        Assert.Equal(120, saved.GetProperty("hourlyRate").GetDecimal());
        Assert.False(saved.GetProperty("isDefault").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings/cost", new { currency = "dollars", hourlyRate = 120 })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/settings/cost")).StatusCode);
        var reset = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/settings/cost");
        Assert.True(reset.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task Tags_round_trip_and_show_up_in_the_portfolio()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var created = await CreateAssessmentAsync(client, "Tagged");

        var put = await client.PutAsJsonAsync($"/api/assessments/{created!.Id}/tags", new { tags = new[] { " billing ", "Client-X", "billing" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var assessment = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/assessments/{created.Id}");
        var tags = assessment.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(["billing", "Client-X"], tags); // trimmed, deduped case-insensitively

        var portfolio = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/portfolio?lang=en");
        var row = portfolio.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == created.Id);
        Assert.Contains("billing", row.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Policies_accept_expiry_and_reject_past_dates()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var future = DateTimeOffset.UtcNow.AddDays(30);
        var created = await client.PostAsJsonAsync("/api/policies", new { rulePattern = "quality.file.large", pathGlob = (string?)null, reason = "seasonal", author = "ana", expiresAtUtc = future });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.NotNull(body.GetProperty("expiresAtUtc").GetString());

        var past = await client.PostAsJsonAsync("/api/policies", new { rulePattern = "quality.file.large", pathGlob = (string?)null, reason = "late", author = "ana", expiresAtUtc = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
    }

    private const string Sarif = """
        {
          "version": "2.1.0",
          "runs": [ {
            "tool": { "driver": { "name": "eslint", "version": "9.0.0" } },
            "results": [
              { "ruleId": "no-eval", "level": "error", "message": { "text": "eval can be harmful." },
                "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "src/app.js" }, "region": { "startLine": 3 } } } ] },
              { "ruleId": "prefer-const", "level": "note", "message": { "text": "Use const." },
                "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "src/app.js" }, "region": { "startLine": 9 } } } ] }
            ]
          } ]
        }
        """;

    [Fact]
    public async Task Sarif_import_creates_a_run_reconciles_and_resolves_on_reimport()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var created = await CreateAssessmentAsync(client, "External findings");

        var first = await client.PostAsync($"/api/assessments/{created!.Id}/sarif", new StringContent(Sarif, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var result = await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("eslint", result.GetProperty("tool").GetString());
        Assert.Equal(2, result.GetProperty("imported").GetInt32());
        Assert.Equal(2, result.GetProperty("newFindings").GetInt32());

        // Second import without prefer-const: it resolves; no-eval recurs.
        var smaller = Sarif.Replace("""
              { "ruleId": "prefer-const", "level": "note", "message": { "text": "Use const." },
                "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "src/app.js" }, "region": { "startLine": 9 } } } ] }
        """, "").Replace("] },\n\n            ]", "] }\n            ]").Replace(",\n            \n          ]", "\n          ]");
        smaller = smaller.Replace(",\r\n            \r\n", "\r\n").Replace(",\n            \n", "\n");
        // Robust: rebuild the trimmed log instead of string surgery.
        smaller = """
        {
          "version": "2.1.0",
          "runs": [ {
            "tool": { "driver": { "name": "eslint", "version": "9.0.0" } },
            "results": [
              { "ruleId": "no-eval", "level": "error", "message": { "text": "eval can be harmful." },
                "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "src/app.js" }, "region": { "startLine": 3 } } } ] }
            ]
          } ]
        }
        """;

        var second = await client.PostAsync($"/api/assessments/{created.Id}/sarif", new StringContent(smaller, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var again = await second.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, again.GetProperty("newFindings").GetInt32());
        Assert.Equal(1, again.GetProperty("recurring").GetInt32());
        Assert.Equal(1, again.GetProperty("resolved").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/assessments/{created.Id}/sarif", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
    }
}
