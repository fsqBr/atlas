extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>AI provider settings (key write-only) and queuing of business-rule analyses.</summary>
public sealed class AiSettingsAndBusinessRulesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    private sealed record Settings(bool Configured, bool SecretStoreConfigured, string Provider, string Model, string? BaseUrl, bool HasKey, bool RequiresKey, bool Enabled, bool Usable, int MaxSnippetsPerAnalysis);
    private sealed record Created(Guid Id, Guid JobId);
    private sealed record Rules(bool AiUsable, List<object> Analyses, List<object> RulesList);
    private sealed record Job(Guid Id, Guid AssessmentId, string Kind, string State);

    [Fact]
    public async Task Settings_round_trip_never_returns_the_key_and_gates_analysis()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var initial = await client.GetFromJsonAsync<Settings>("/api/ai/settings");
        Assert.True(initial!.SecretStoreConfigured);
        Assert.Equal("Anthropic", initial.Provider);

        // Enabling a hosted provider without a key is refused.
        var noKey = await client.PutAsJsonAsync("/api/ai/settings", new { provider = "OpenAI", enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);

        var saved = await client.PutAsJsonAsync("/api/ai/settings", new { provider = "Anthropic", model = "claude-opus-5", apiKey = "sk-ant-very-secret", enabled = true, maxSnippetsPerAnalysis = 12 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var raw = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("very-secret", raw);
        var settings = await saved.Content.ReadFromJsonAsync<Settings>();
        Assert.True(settings!.HasKey);
        Assert.True(settings.Usable);
        Assert.Equal("claude-opus-5", settings.Model);
        Assert.Equal(12, settings.MaxSnippetsPerAnalysis);

        // Saving again without a key keeps the stored one; switching provider drops it.
        var kept = await (await client.PutAsJsonAsync("/api/ai/settings", new { provider = "Anthropic", enabled = true })).Content.ReadFromJsonAsync<Settings>();
        Assert.True(kept!.HasKey);
        var switched = await (await client.PutAsJsonAsync("/api/ai/settings", new { provider = "Ollama", enabled = true })).Content.ReadFromJsonAsync<Settings>();
        Assert.False(switched!.HasKey);
        Assert.True(switched.Usable);
        Assert.Equal("http://host.docker.internal:11434/v1", switched.BaseUrl);

        var cleared = await client.DeleteAsync("/api/ai/settings/key");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        // Disable and check the analysis endpoint refuses with 412.
        await client.PutAsJsonAsync("/api/ai/settings", new { provider = "Ollama", enabled = false });
        var created = await (await client.PostAsJsonAsync("/api/assessments", new { name = "AI gate", sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })).Content.ReadFromJsonAsync<Created>();
        var refused = await client.PostAsync($"/api/assessments/{created!.Id}/business-rules/analyze", null);
        Assert.Equal(HttpStatusCode.PreconditionFailed, refused.StatusCode);

        var rules = await client.GetAsync($"/api/assessments/{created.Id}/business-rules?lang=pt");
        Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
        Assert.Contains("\"aiUsable\":false", await rules.Content.ReadAsStringAsync());

        // Narratives are gated the same way; no summary exists yet.
        var summary = await client.GetAsync($"/api/assessments/{created.Id}/summary?lang=pt");
        Assert.Equal(HttpStatusCode.NoContent, summary.StatusCode);
        var generate = await client.PostAsync($"/api/assessments/{created.Id}/summary/generate?lang=pt", null);
        Assert.Equal(HttpStatusCode.PreconditionFailed, generate.StatusCode);
        var explainMissing = await client.PostAsync($"/api/assessments/{created.Id}/findings/{Guid.NewGuid()}/explain", null);
        Assert.Equal(HttpStatusCode.NotFound, explainMissing.StatusCode);
        var fixMissing = await client.PostAsync($"/api/assessments/{created.Id}/findings/{Guid.NewGuid()}/fix?lang=pt", null);
        Assert.Equal(HttpStatusCode.NotFound, fixMissing.StatusCode);
        var fixGetMissing = await client.GetAsync($"/api/assessments/{created.Id}/findings/{Guid.NewGuid()}/fix");
        Assert.Equal(HttpStatusCode.NotFound, fixGetMissing.StatusCode);

        // The PR comment renders even before the first run finishes (and ?ai=true degrades silently when AI is off).
        var comment = await client.GetAsync($"/api/assessments/{created.Id}/pr-comment?failOn=High&minScore=60&ai=true");
        Assert.Equal(HttpStatusCode.OK, comment.StatusCode);
        Assert.StartsWith("text/markdown", comment.Content.Headers.ContentType?.MediaType);
        var markdown = await comment.Content.ReadAsStringAsync();
        Assert.StartsWith(Atlas.Application.Assessments.PrComment.Marker, markdown);
        Assert.Contains("no completed run", markdown);
        Assert.Contains("Atlas v0.", markdown);
        var badGate = await client.GetAsync($"/api/assessments/{created.Id}/pr-comment?failOn=Enormous");
        Assert.Equal(HttpStatusCode.BadRequest, badGate.StatusCode);

        // Feedback: nothing to rate yet → 404; unknown kind → 400; business rule missing → 404; the quality summary is empty but answers.
        var noPlan = await client.PutAsJsonAsync($"/api/assessments/{created.Id}/ai/feedback?kind=migration-plan&lang=pt", new { rating = 1, comment = (string?)null, author = "ci" });
        Assert.Equal(HttpStatusCode.NotFound, noPlan.StatusCode);
        var badKind = await client.PutAsJsonAsync($"/api/assessments/{created.Id}/ai/feedback?kind=poem", new { rating = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        var noRule = await client.PutAsJsonAsync($"/api/assessments/{created.Id}/business-rules/{Guid.NewGuid()}/feedback", new { rating = -1, comment = "nope" });
        Assert.Equal(HttpStatusCode.NotFound, noRule.StatusCode);
        var quality = await client.GetAsync("/api/ai/feedback");
        Assert.Equal(HttpStatusCode.OK, quality.StatusCode);
        Assert.Contains("\"byKind\":[]", await quality.Content.ReadAsStringAsync());

        // Migration plan: nothing drafted, export has nothing to give, and drafting needs a completed run (inventory) before it even asks the model.
        var plan = await client.GetAsync($"/api/assessments/{created.Id}/migration-plan?lang=pt");
        Assert.Equal(HttpStatusCode.NoContent, plan.StatusCode);
        var planExport = await client.GetAsync($"/api/assessments/{created.Id}/migration-plan/export?lang=pt");
        Assert.Equal(HttpStatusCode.NotFound, planExport.StatusCode);
        var planGenerate = await client.PostAsync($"/api/assessments/{created.Id}/migration-plan/generate?lang=pt", null);
        Assert.Equal(HttpStatusCode.Conflict, planGenerate.StatusCode);
        Assert.Contains("run the assessment", await planGenerate.Content.ReadAsStringAsync());
        var planMissing = await client.GetAsync($"/api/assessments/{Guid.NewGuid()}/migration-plan/export");
        Assert.Equal(HttpStatusCode.NotFound, planMissing.StatusCode);

        var estimate = await client.GetAsync("/api/ai/estimate?methods=8");
        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
        Assert.Contains("\"requests\":2", await estimate.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Analysis_is_queued_as_its_own_job_kind_when_ai_is_usable()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        await client.PutAsJsonAsync("/api/ai/settings", new { provider = "Ollama", baseUrl = "http://127.0.0.1:9", enabled = true });
        var created = await (await client.PostAsJsonAsync("/api/assessments", new { name = "AI job", sourceKind = "local", sourceLocator = Path.GetTempPath(), branch = (string?)null })).Content.ReadFromJsonAsync<Created>();

        // The creation scan job is still queued → 409 (one job at a time per assessment).
        var busy = await client.PostAsync($"/api/assessments/{created!.Id}/business-rules/analyze", null);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);

        var jobs = await client.GetFromJsonAsync<List<Job>>("/api/jobs");
        var scan = jobs!.Single(j => j.AssessmentId == created.Id);
        Assert.Equal("scan", scan.Kind);

        // Test the connection path returns a clean failure (nothing listens on port 9) instead of an exception.
        var test = await client.PostAsync("/api/ai/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.Contains("\"succeeded\":false", await test.Content.ReadAsStringAsync());
    }
}
