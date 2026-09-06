using System.Net;
using System.Text.Json.Nodes;
using Atlas.Ai;
using Atlas.Application.Ai;
using Atlas.Domain.Ai;

namespace Atlas.Application.Tests;

public class ChatClientTests
{
    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Anthropic_request_carries_key_header_and_parses_text_and_usage()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"model":"claude-sonnet-5","content":[{"type":"text","text":"[]"}],"usage":{"input_tokens":120,"output_tokens":3}}""");
        var client = new AnthropicChatClient(new HttpClient(handler), "claude-sonnet-5", "https://api.anthropic.com", "sk-ant-secret");

        var result = await client.CompleteAsync(new ChatRequest("sys", "user", MaxTokens: 512), CancellationToken.None);

        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("sk-ant-secret", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicChatClient.ApiVersion, handler.Request.Headers.GetValues("anthropic-version").Single());
        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal("claude-sonnet-5", body["model"]!.GetValue<string>());
        Assert.Equal(512, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("sys", body["system"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("[]", result.Text);
        Assert.Equal(120, result.InputTokens);
        Assert.Equal(3, result.OutputTokens);
    }

    [Fact]
    public async Task OpenAI_uses_bearer_and_max_completion_tokens()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"model":"gpt-4.1-mini","choices":[{"message":{"role":"assistant","content":"OK"}}],"usage":{"prompt_tokens":10,"completion_tokens":1}}""");
        var client = new OpenAiCompatibleChatClient(new HttpClient(handler), AiProvider.OpenAI, "gpt-4.1-mini", "https://api.openai.com/v1", "sk-openai");

        var result = await client.CompleteAsync(new ChatRequest("sys", "hi", MaxTokens: 16), CancellationToken.None);

        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-openai", handler.Request.Headers.Authorization!.ToString());
        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal(16, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(body["max_tokens"]);
        Assert.Equal("gpt-4.1-mini", body["model"]!.GetValue<string>());
        Assert.Equal("OK", result.Text);
        Assert.Equal(10, result.InputTokens);
    }

    [Fact]
    public async Task Azure_uses_deployment_url_and_api_key_header_without_model_in_body()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"OK"}}]}""");
        var client = new OpenAiCompatibleChatClient(new HttpClient(handler), AiProvider.AzureOpenAI, "my-deploy", "https://res.openai.azure.com", "azkey");

        await client.CompleteAsync(new ChatRequest("s", "u"), CancellationToken.None);

        Assert.Equal($"https://res.openai.azure.com/openai/deployments/my-deploy/chat/completions?api-version={OpenAiCompatibleChatClient.AzureApiVersion}", handler.Request!.RequestUri!.ToString());
        Assert.Equal("azkey", handler.Request.Headers.GetValues("api-key").Single());
        Assert.Null(handler.Request.Headers.Authorization);
        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Null(body["model"]);
        Assert.NotNull(body["max_tokens"]);
    }

    [Fact]
    public async Task Ollama_needs_no_key_and_uses_max_tokens()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"model":"llama3.1","choices":[{"message":{"content":"OK"}}]}""");
        var client = new OpenAiCompatibleChatClient(new HttpClient(handler), AiProvider.Ollama, "llama3.1", "http://host.docker.internal:11434/v1", null);

        var result = await client.CompleteAsync(new ChatRequest("s", "u"), CancellationToken.None);

        Assert.Null(handler.Request!.Headers.Authorization);
        Assert.NotNull(JsonNode.Parse(handler.RequestBody!)!["max_tokens"]);
        Assert.Equal("llama3.1", result.Model);
    }

    [Fact]
    public async Task Provider_errors_become_ChatProviderException_with_status_and_message_but_no_key()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, """{"error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        var client = new AnthropicChatClient(new HttpClient(handler), "m", "https://api.anthropic.com", "sk-ant-secret");

        var ex = await Assert.ThrowsAsync<ChatProviderException>(() => client.CompleteAsync(new ChatRequest("s", "u"), CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("invalid x-api-key", ex.Message);
        Assert.DoesNotContain("sk-ant-secret", ex.Message);
    }

    [Fact]
    public void Factory_refuses_hosted_providers_without_a_key_and_builds_the_right_client()
    {
        var settings = new AiProviderSettings(Guid.NewGuid(), Guid.NewGuid());
        var factory = new ChatClientFactory(new SingleClientFactory());

        Assert.Throws<InvalidOperationException>(() => factory.Create(settings, null));
        Assert.IsType<AnthropicChatClient>(factory.Create(settings, "k"));

        settings.Configure(AiProvider.Ollama, null, null, null, enabled: true, null);
        Assert.IsType<OpenAiCompatibleChatClient>(factory.Create(settings, null));
        Assert.True(settings.IsUsable);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
