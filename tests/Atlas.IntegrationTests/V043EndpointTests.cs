extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>v0.43: per-tenant notification settings, trend by tag, jobs SSE stream.</summary>
public sealed class V043EndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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
    public async Task Notification_settings_round_trip_and_never_echo_the_secret()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var defaults = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/settings/notifications");
        Assert.True(defaults.GetProperty("isDefault").GetBoolean());

        var put = await client.PutAsJsonAsync("/api/settings/notifications", new
        {
            webhookUrl = "https://hooks.example.com/atlas",
            secret = "sup3r-s3cret",
            slackWebhookUrl = (string?)null,
            teamsWebhookUrl = (string?)null,
            digestDayOfWeek = "Monday",
            digestHourUtc = 9,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(saved.GetProperty("secretSet").GetBoolean());
        Assert.False(saved.TryGetProperty("secret", out var echoed) && echoed.ValueKind == System.Text.Json.JsonValueKind.String);
        Assert.Equal("Monday", saved.GetProperty("digestDayOfWeek").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings/notifications", new { digestDayOfWeek = "Someday" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/settings/notifications", new { webhookUrl = "not-a-url" })).StatusCode);

        // Clearing every field removes the row and falls back to the deployment defaults.
        // The secret is write-only, so null means "keep": clearing it takes an explicit empty string.
        var cleared = await client.PutAsJsonAsync("/api/settings/notifications", new { secret = "" });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.True((await cleared.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task Trend_accepts_a_tag_filter()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var all = await client.GetAsync("/api/portfolio/trend?weeks=8");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var filtered = await client.GetAsync("/api/portfolio/trend?weeks=8&tag=nonexistent-tag");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var points = await filtered.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(0, points.GetArrayLength()); // no assessment carries the tag
    }

    [Fact]
    public async Task Jobs_event_stream_sends_the_first_frame()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);

        using var response = await client.GetAsync("/api/events/jobs", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("data: ", line);
    }
}
