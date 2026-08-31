extern alias AtlasApi;

using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

public sealed class VersionEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Version_endpoint_reports_the_assembly_version()
    {
        using var factory = new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
            builder.UseSetting("Atlas:AutoMigrate", "false");
            builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
            builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(new byte[32]));
        });
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/version");
        var version = body.GetProperty("version").GetString();
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
    }
}
