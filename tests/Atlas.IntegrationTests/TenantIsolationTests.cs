extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>rows of one tenant are invisible to another; the default tenant keeps working untouched.</summary>
public sealed class TenantIsolationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    private sealed record TenantRow(Guid Id, string Name, string? ExternalKey, DateTimeOffset CreatedAtUtc, bool IsDefault);
    private sealed record Created(Guid Id, Guid JobId);
    private sealed record Row(Guid Id, string Name);
    private sealed record Me(string? Name, Guid? TenantId, string? TenantName, bool IsDefaultTenant, string[] Roles);

    private static HttpClient ForTenant(WebApplicationFactory<ApiProgram> factory, string? key)
    {
        var client = factory.CreateClient();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Atlas-Tenant", key);
        }

        return client;
    }

    [Fact]
    public async Task Assessments_credentials_and_ai_settings_are_scoped_per_tenant()
    {
        using var factory = Factory();
        var key = "acme-" + Guid.NewGuid().ToString("N")[..8];
        using var admin = ForTenant(factory, null);

        var created = await admin.PostAsJsonAsync("/api/tenants", new { name = "Acme Corp", externalKey = key });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tenant = await created.Content.ReadFromJsonAsync<TenantRow>();
        Assert.False(tenant!.IsDefault);

        var duplicate = await admin.PostAsJsonAsync("/api/tenants", new { name = "Acme again", externalKey = key });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var list = await admin.GetFromJsonAsync<List<TenantRow>>("/api/tenants");
        Assert.Contains(list!, t => t.IsDefault);
        Assert.Contains(list!, t => t.Id == tenant.Id);

        using var acme = ForTenant(factory, key);
        var me = await acme.GetFromJsonAsync<Me>("/api/auth/me");
        Assert.Equal(tenant.Id, me!.TenantId);
        Assert.Equal("Acme Corp", me.TenantName);
        Assert.False(me.IsDefaultTenant);

        var name = "Acme app " + Guid.NewGuid().ToString("N")[..6];
        var acmeAssessment = await (await acme.PostAsJsonAsync("/api/assessments", new { name, sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })).Content.ReadFromJsonAsync<Created>();

        // Visible to Acme, invisible to the default tenant — by list, by id, and through dependent endpoints.
        var acmeRows = await acme.GetFromJsonAsync<List<Row>>("/api/assessments");
        Assert.Contains(acmeRows!, r => r.Id == acmeAssessment!.Id);
        var defaultRows = await admin.GetFromJsonAsync<List<Row>>("/api/assessments");
        Assert.DoesNotContain(defaultRows!, r => r.Id == acmeAssessment!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/assessments/{acmeAssessment!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/assessments/{acmeAssessment.Id}/findings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync($"/api/assessments/{acmeAssessment.Id}")).StatusCode);

        // Credentials are per tenant too.
        await acme.PutAsJsonAsync("/api/credentials/acme-git", new { secret = "s3cret", username = "bot", description = (string?)null });
        var acmeCreds = await acme.GetAsync("/api/credentials");
        Assert.Contains("acme-git", await acmeCreds.Content.ReadAsStringAsync());
        var defaultCreds = await admin.GetAsync("/api/credentials");
        Assert.DoesNotContain("acme-git", await defaultCreds.Content.ReadAsStringAsync());

        // AI settings are per tenant: Acme enables Ollama, default stays untouched.
        await acme.PutAsJsonAsync("/api/ai/settings", new { provider = "Ollama", enabled = true });
        var acmeAi = await acme.GetAsync("/api/ai/settings");
        Assert.Contains("\"usable\":true", await acmeAi.Content.ReadAsStringAsync());
        var defaultAi = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/ai/settings");
        Assert.NotEqual("Ollama", defaultAi.GetProperty("provider").GetString() == "Ollama" && defaultAi.GetProperty("enabled").GetBoolean() ? "Ollama" : "other");

        // Unknown tenant header is refused; the default tenant is used when no header is sent.
        using var unknown = ForTenant(factory, "nobody-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.BadRequest, (await unknown.GetAsync("/api/assessments")).StatusCode);
        var defaultMe = await admin.GetFromJsonAsync<Me>("/api/auth/me");
        Assert.True(defaultMe!.IsDefaultTenant);
    }
}
