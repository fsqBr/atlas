extern alias AtlasApi;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atlas.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>Per-assessment sharing: restricted assessments are invisible to non-members and read-only for viewers.</summary>
public sealed class AssessmentAccessTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private WebApplicationFactory<ApiProgram> Factory() => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
        builder.UseSetting("Atlas:AutoMigrate", "false");
        builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
        builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Operations:RateLimitPerMinute", "1000");
        builder.UseSetting("Atlas:Auth:Enabled", "true");
        builder.UseSetting("Atlas:Auth:Authority", "https://idp.example.invalid/realms/atlas");
        builder.UseSetting("Atlas:Auth:ClientId", "atlas-web");
        builder.UseSetting("Atlas:Auth:RequireHttpsMetadata", "false");
    });

    private sealed record Created(Guid Id, Guid JobId);
    private sealed record Row(Guid Id, string Name);
    private sealed record Access(bool Restricted, string? MyRole, bool CanManage, bool CanEdit, List<Entry> Entries);
    private sealed record Entry(Guid Id, string Subject, string? SubjectName, string Role);

    private static HttpClient With(WebApplicationFactory<ApiProgram> factory, string secret)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    [Fact]
    public async Task Restricting_an_assessment_hides_it_from_others_and_makes_viewers_read_only()
    {
        using var factory = Factory();
        string adminSecret, aliceSecret, bobSecret;
        Guid aliceId, bobId;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AtlasApi::Atlas.Api.HttpTenantContext>().UseSystemScope();
            var tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            adminSecret = (await tokens.CreateAsync("admin", "admin", "t", null, CancellationToken.None)).Secret;
            var alice = await tokens.CreateAsync("alice", "analyst", "t", null, CancellationToken.None);
            var bob = await tokens.CreateAsync("bob", "analyst", "t", null, CancellationToken.None);
            (aliceSecret, aliceId) = (alice.Secret, alice.Token.Id);
            (bobSecret, bobId) = (bob.Secret, bob.Token.Id);
        }

        using var admin = With(factory, adminSecret);
        using var aliceClient = With(factory, aliceSecret);
        using var bobClient = With(factory, bobSecret);

        var name = "Shared " + Guid.NewGuid().ToString("N")[..6];
        var created = await (await aliceClient.PostAsJsonAsync("/api/assessments", new { name, sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })).Content.ReadFromJsonAsync<Created>();

        // Open by default: everyone in the tenant sees it.
        Assert.Contains((await bobClient.GetFromJsonAsync<List<Row>>("/api/assessments"))!, r => r.Id == created!.Id);
        var open = await aliceClient.GetFromJsonAsync<Access>($"/api/assessments/{created!.Id}/access");
        Assert.False(open!.Restricted);
        Assert.True(open.CanEdit);

        // Alice restricts it to herself (auto-owner) + a viewer; Bob is not listed.
        var restricted = await (await aliceClient.PutAsJsonAsync($"/api/assessments/{created.Id}/access", new { subject = "carol@example.test", subjectName = "Carol", role = "viewer" })).Content.ReadFromJsonAsync<Access>();
        Assert.True(restricted!.Restricted);
        Assert.Equal("Owner", restricted.MyRole);
        Assert.Contains(restricted.Entries, e => e.Subject == $"token:{aliceId}" && e.Role == "Owner");
        Assert.Contains(restricted.Entries, e => e.Subject == "carol@example.test" && e.Role == "Viewer");

        Assert.DoesNotContain((await bobClient.GetFromJsonAsync<List<Row>>("/api/assessments"))!, r => r.Id == created.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.GetAsync($"/api/assessments/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.GetAsync($"/api/assessments/{created.Id}/findings")).StatusCode);

        // Admins always see it and can manage it.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/assessments/{created.Id}")).StatusCode);
        var adminView = await admin.GetFromJsonAsync<Access>($"/api/assessments/{created.Id}/access");
        Assert.True(adminView!.CanManage);

        // Bob gets viewer access: he can read but not change; then editor: he can change.
        await aliceClient.PutAsJsonAsync($"/api/assessments/{created.Id}/access", new { subject = $"token:{bobId}", subjectName = "Bob", role = "viewer" });
        Assert.Equal(HttpStatusCode.OK, (await bobClient.GetAsync($"/api/assessments/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bobClient.PatchAsJsonAsync($"/api/assessments/{created.Id}", new { name = "hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bobClient.PutAsJsonAsync($"/api/assessments/{created.Id}/access", new { subject = "eve@example.test", role = "owner" })).StatusCode);

        await aliceClient.PutAsJsonAsync($"/api/assessments/{created.Id}/access", new { subject = $"token:{bobId}", role = "editor" });
        Assert.Equal(HttpStatusCode.OK, (await bobClient.PatchAsJsonAsync($"/api/assessments/{created.Id}", new { name = name + " (edited)" })).StatusCode);

        // Removing every entry opens it again.
        var view = await aliceClient.GetFromJsonAsync<Access>($"/api/assessments/{created.Id}/access");
        foreach (var entry in view!.Entries.Where(e => e.Role != "Owner"))
        {
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.DeleteAsync($"/api/assessments/{created.Id}/access/{entry.Id}")).StatusCode);
        }

        var lastOwner = (await aliceClient.GetFromJsonAsync<Access>($"/api/assessments/{created.Id}/access"))!.Entries.Single();
        var reopened = await (await aliceClient.DeleteAsync($"/api/assessments/{created.Id}/access/{lastOwner.Id}")).Content.ReadFromJsonAsync<Access>();
        Assert.False(reopened!.Restricted);
        Assert.Equal(HttpStatusCode.OK, (await bobClient.GetAsync($"/api/assessments/{created.Id}")).StatusCode);
    }
}
