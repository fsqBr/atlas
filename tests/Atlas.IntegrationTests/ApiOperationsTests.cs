extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>Audit trail, metrics endpoint and RBAC rules of the API host.</summary>
public sealed class ApiOperationsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    private sealed record AuditRow(long Id, DateTimeOffset AtUtc, string Actor, string Method, string Path, int StatusCode, Guid? AssessmentId, string? Detail);

    [Fact]
    public async Task Mutating_calls_are_audited_and_reads_are_not()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var marker = "audit-" + Guid.NewGuid().ToString("N")[..8];
        var created = await client.PostAsJsonAsync("/api/policies", new { rulePattern = "quality.file.large", pathGlob = (string?)null, reason = marker, author = "tester" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/assessments")).StatusCode);

        var audit = await client.GetFromJsonAsync<List<AuditRow>>("/api/audit?take=50");
        var entry = Assert.Single(audit!, a => a.Method == "POST" && a.Path == "/api/policies" && a.StatusCode == 201 && a.AtUtc > DateTimeOffset.UtcNow.AddMinutes(-1) && a.Actor == "anonymous");
        Assert.DoesNotContain(audit!, a => a.Method == "GET");
        Assert.Null(entry.AssessmentId);
    }

    [Fact]
    public async Task Metrics_endpoint_exposes_atlas_gauges()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var metrics = await client.GetStringAsync("/metrics");
        Assert.Contains("atlas_scan_jobs", metrics);
        Assert.Contains("atlas_assessments", metrics);
        Assert.Contains("atlas_open_findings", metrics);
    }

    [Fact]
    public void Rbac_maps_paths_to_roles()
    {
        var options = new Atlas.Api.AuthOptions();
        Assert.Equal("atlas-admin", Atlas.Api.AuthSetup.RequiredRole(options, "PUT", "/api/credentials/gh"));
        Assert.Equal("atlas-admin", Atlas.Api.AuthSetup.RequiredRole(options, "POST", "/api/policies"));
        Assert.Equal("atlas-admin", Atlas.Api.AuthSetup.RequiredRole(options, "DELETE", "/api/assessments/" + Guid.NewGuid()));
        Assert.Equal("atlas-analyst", Atlas.Api.AuthSetup.RequiredRole(options, "POST", "/api/assessments"));
        Assert.Equal("atlas-analyst", Atlas.Api.AuthSetup.RequiredRole(options, "POST", $"/api/assessments/{Guid.NewGuid()}/policies"));
        Assert.Equal("atlas-analyst", Atlas.Api.AuthSetup.RequiredRole(options, "POST", $"/api/assessments/{Guid.NewGuid()}/runs"));

        var analyst = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("roles", "atlas-analyst")], "test"));
        var admin = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("roles", "atlas-admin")], "test"));
        var viewer = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("sub", "x")], "test"));
        Assert.True(Atlas.Api.AuthSetup.HasRole(analyst, options, "atlas-analyst"));
        Assert.False(Atlas.Api.AuthSetup.HasRole(analyst, options, "atlas-admin"));
        Assert.True(Atlas.Api.AuthSetup.HasRole(admin, options, "atlas-analyst"));
        Assert.True(Atlas.Api.AuthSetup.HasRole(admin, options, "atlas-admin"));
        Assert.False(Atlas.Api.AuthSetup.HasRole(viewer, options, "atlas-analyst"));
    }
}
