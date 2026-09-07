extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>The real API host booted in-process: OIDC gate on and off.</summary>
public sealed class ApiAuthTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private WebApplicationFactory<ApiProgram> Factory(bool authEnabled) => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
        builder.UseSetting("Atlas:AutoMigrate", "false");
        builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
        builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Auth:Enabled", authEnabled ? "true" : "false");
        builder.UseSetting("Atlas:Auth:Authority", "https://login.microsoftonline.com/common/v2.0");
        builder.UseSetting("Atlas:Auth:ClientId", "atlas-web-test");
    });

    [Fact]
    public async Task With_auth_off_the_api_is_open_and_says_so()
    {
        using var factory = Factory(authEnabled: false);
        using var client = factory.CreateClient();

        var config = await client.GetFromJsonAsync<AuthConfigDto>("/api/auth/config");
        Assert.False(config!.Enabled);
        Assert.Null(config.Authority);

        var list = await client.GetAsync("/api/assessments");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task With_auth_on_api_routes_need_a_token_but_health_and_config_stay_open()
    {
        using var factory = Factory(authEnabled: true);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        var config = await client.GetFromJsonAsync<AuthConfigDto>("/api/auth/config");
        Assert.True(config!.Enabled);
        Assert.Equal("atlas-web-test", config.ClientId);
        Assert.Equal("https://login.microsoftonline.com/common/v2.0", config.Authority);

        var anonymous = await client.GetAsync("/api/assessments");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("Bearer", anonymous.Headers.WwwAuthenticate.ToString());

        using var forged = new HttpRequestMessage(HttpMethod.Get, "/api/credentials");
        forged.Headers.Authorization = new("Bearer", "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(forged)).StatusCode);
    }

    private sealed record AuthConfigDto(bool Enabled, string? Authority, string? ClientId, string? Scopes);
}
