using Atlas.Ai;

namespace Atlas.Application.Tests;

public class LocalOllamaProbeTests
{
    [Fact]
    public void Parses_ollama_tags_into_sorted_model_names()
    {
        var models = LocalOllamaProbe.ParseModels("""{"models":[{"name":"qwen2.5-coder:7b","size":4683087332},{"name":"llama3.1:8b"}]}""");
        Assert.Equal(["llama3.1:8b", "qwen2.5-coder:7b"], models);
        Assert.Empty(LocalOllamaProbe.ParseModels("""{"models":[]}"""));
        Assert.Empty(LocalOllamaProbe.ParseModels("not json"));
    }

    [Fact]
    public async Task Probe_without_url_reports_unavailable_and_unreachable_url_fails_soft()
    {
        var none = new LocalOllamaProbe(new SingleClientFactory(), new LocalAiOptions());
        var status = await none.ProbeAsync(CancellationToken.None);
        Assert.Null(status.Url);
        Assert.False(status.Available);
        Assert.Equal("qwen2.5-coder:7b", status.DefaultModel);

        var down = new LocalOllamaProbe(new SingleClientFactory(), new LocalAiOptions { LocalOllamaUrl = "http://127.0.0.1:9/" });
        var s2 = await down.ProbeAsync(CancellationToken.None);
        Assert.Equal("http://127.0.0.1:9", s2.Url);
        Assert.False(s2.Available);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
