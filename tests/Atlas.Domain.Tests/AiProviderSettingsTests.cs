using Atlas.Domain.Ai;
using Atlas.Domain.Jobs;

namespace Atlas.Domain.Tests;

public class AiProviderSettingsTests
{
    [Fact]
    public void Hosted_providers_are_unusable_without_a_key_and_ollama_is_usable_without_one()
    {
        var settings = new AiProviderSettings(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(AiProvider.Anthropic, settings.Provider);
        Assert.Equal("claude-sonnet-5", settings.Model);

        settings.Configure(AiProvider.Anthropic, null, null, null, enabled: true, 40);
        Assert.True(settings.Enabled);
        Assert.False(settings.IsUsable);

        settings.Configure(AiProvider.Anthropic, "claude-opus-5", null, [1, 2, 3], enabled: true, 40);
        Assert.True(settings.IsUsable);
        Assert.Equal("claude-opus-5", settings.Model);

        settings.Configure(AiProvider.Ollama, null, "http://host.docker.internal:11434/v1/", null, enabled: true, 10);
        Assert.True(settings.IsUsable);
        Assert.False(settings.HasKey); // switching provider discards the previous key
        Assert.Equal("http://host.docker.internal:11434/v1", settings.BaseUrl);
        Assert.Equal("llama3.1", settings.Model);
    }

    [Fact]
    public void Keeps_the_stored_key_when_the_same_provider_is_saved_without_one()
    {
        var settings = new AiProviderSettings(Guid.NewGuid(), Guid.NewGuid());
        settings.Configure(AiProvider.OpenAI, null, null, [9], enabled: true, null);
        settings.Configure(AiProvider.OpenAI, "gpt-4.1", null, null, enabled: true, null);

        Assert.True(settings.HasKey);
        Assert.Equal("gpt-4.1", settings.Model);
    }

    [Fact]
    public void Validates_urls_snippet_limits_and_azure_endpoint()
    {
        var settings = new AiProviderSettings(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => settings.Configure(AiProvider.OpenAI, null, "not a url", null, false, null));
        Assert.Throws<ArgumentException>(() => settings.Configure(AiProvider.OpenAI, null, "ftp://x", null, false, null));
        Assert.Throws<ArgumentException>(() => settings.Configure(AiProvider.OpenAI, null, null, null, false, 0));
        Assert.Throws<ArgumentException>(() => settings.Configure(AiProvider.AzureOpenAI, "dep", null, null, false, null));
        settings.Configure(AiProvider.AzureOpenAI, "dep", "https://res.openai.azure.com", [1], true, 5);
        Assert.True(settings.IsUsable);
    }

    [Fact]
    public void Test_results_are_recorded_and_reset_on_reconfiguration()
    {
        var settings = new AiProviderSettings(Guid.NewGuid(), Guid.NewGuid());
        settings.RecordTest(true, "Connected");
        Assert.True(settings.LastTestSucceeded);
        settings.Configure(AiProvider.Anthropic, null, null, null, false, null);
        Assert.Null(settings.LastTestSucceeded);
    }

    [Fact]
    public void Jobs_default_to_scan_kind_and_reject_unknown_kinds()
    {
        var job = new ScanJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(ScanJob.Kinds.Scan, job.Kind);
        Assert.Equal(ScanJob.Kinds.BusinessRules, new ScanJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ScanJob.Kinds.BusinessRules).Kind);
        Assert.Throws<ArgumentException>(() => new ScanJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "weird"));
    }
}
