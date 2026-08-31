extern alias AtlasApi;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>Service tokens: management endpoints, and authentication next to OIDC when auth is on.</summary>
public sealed class ApiTokenTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private WebApplicationFactory<ApiProgram> Factory(bool authEnabled) => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
        builder.UseSetting("Atlas:AutoMigrate", "false");
        builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
        builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Operations:RateLimitPerMinute", "1000");
        if (authEnabled)
        {
            builder.UseSetting("Atlas:Auth:Enabled", "true");
            builder.UseSetting("Atlas:Auth:Authority", "https://idp.example.invalid/realms/atlas");
            builder.UseSetting("Atlas:Auth:ClientId", "atlas-web");
            builder.UseSetting("Atlas:Auth:RequireHttpsMetadata", "false");
        }
    });

    private sealed record TokenRow(Guid Id, string Name, string Hint, string Role, string CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? LastUsedAtUtc, DateTimeOffset? RevokedAtUtc, bool Active);
    private sealed record Created(TokenRow Token, string Secret);

    [Fact]
    public async Task Tokens_are_created_once_listed_with_a_hint_and_revoked()
    {
        using var factory = Factory(authEnabled: false);
        using var client = factory.CreateClient();

        var bad = await client.PostAsJsonAsync("/api/tokens", new { name = "ci", role = "root" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var response = await client.PostAsJsonAsync("/api/tokens", new { name = "ci-gate", role = "analyst", expiresAtUtc = DateTimeOffset.UtcNow.AddDays(30) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<Created>();
        Assert.StartsWith("atlas_pat_", created!.Secret);
        Assert.Equal("anonymous", created.Token.CreatedBy);

        var list = await client.GetFromJsonAsync<List<TokenRow>>("/api/tokens");
        var row = list!.Single(t => t.Id == created.Token.Id);
        Assert.True(row.Active);
        Assert.DoesNotContain(created.Secret, await (await client.GetAsync("/api/tokens")).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tokens/{created.Token.Id}")).StatusCode);
        var after = await client.GetFromJsonAsync<List<TokenRow>>("/api/tokens");
        Assert.False(after!.Single(t => t.Id == created.Token.Id).Active);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/tokens/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task With_auth_enabled_a_token_authenticates_with_its_role_and_tenant()
    {
        using var factory = Factory(authEnabled: true);

        // Seed tokens directly (creating one through the API needs an admin, which is what we are testing).
        string analystSecret, adminSecret, revokedSecret;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AtlasApi::Atlas.Api.HttpTenantContext>().UseSystemScope();
            var service = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            analystSecret = (await service.CreateAsync("analyst-ci", "analyst", "test", null, CancellationToken.None)).Secret;
            adminSecret = (await service.CreateAsync("admin-ci", "admin", "test", null, CancellationToken.None)).Secret;
            var revoked = await service.CreateAsync("old", "admin", "test", null, CancellationToken.None);
            revokedSecret = revoked.Secret;
            await service.RevokeAsync(revoked.Token.Id, CancellationToken.None);
        }

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/assessments")).StatusCode);

        using var analyst = factory.CreateClient();
        analyst.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", analystSecret);
        Assert.Equal(HttpStatusCode.OK, (await analyst.GetAsync("/api/assessments")).StatusCode);
        var me = await analyst.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/me");
        Assert.Equal("token:analyst-ci", me.GetProperty("name").GetString());
        Assert.True(me.GetProperty("isDefaultTenant").GetBoolean());
        Assert.Contains("atlas-analyst", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        // analysts cannot manage tokens
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsJsonAsync("/api/tokens", new { name = "x", role = "analyst" })).StatusCode);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSecret);
        var created = await admin.PostAsJsonAsync("/api/tokens", new { name = "from-admin-token", role = "analyst" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("token:admin-ci", (await created.Content.ReadFromJsonAsync<Created>())!.Token.CreatedBy);

        using var stale = factory.CreateClient();
        stale.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", revokedSecret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/assessments")).StatusCode);

        using var garbage = factory.CreateClient();
        garbage.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "atlas_pat_" + new string('a', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, (await garbage.GetAsync("/api/assessments")).StatusCode);
    }
}
