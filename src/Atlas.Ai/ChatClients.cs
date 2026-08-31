using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Application.Ai;
using Atlas.Domain.Ai;

namespace Atlas.Ai;

/// <summary>
/// Anthropic Messages API (https://docs.anthropic.com). Plain HTTP on purpose:
/// no SDK to audit, one request shape to test, and the key only ever travels
/// in the x-api-key header.
/// </summary>
public sealed class AnthropicChatClient(HttpClient http, string model, string baseUrl, string apiKey) : IChatClient
{
    public const string ApiVersion = "2023-06-01";

    public string Provider => AiProvider.Anthropic.ToString();

    public string Model => model;

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/v1/messages");
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", ApiVersion);
        message.Content = JsonContent.Create(new
        {
            model,
            max_tokens = request.MaxTokens,
            temperature = request.Temperature,
            system = request.System,
            messages = new[] { new { role = "user", content = request.User } },
        });

        await message.Content.LoadIntoBufferAsync(cancellationToken); // Content-Length instead of chunked: friendlier to gateways
        using var response = await http.SendAsync(message, cancellationToken);
        var body = await ChatHttp.ReadBodyOrThrowAsync(response, Provider, cancellationToken);

        var text = string.Concat(body["content"]?.AsArray().Select(c => c?["text"]?.GetValue<string>() ?? "") ?? []);
        var usage = body["usage"];
        return new ChatResult(text, usage?["input_tokens"]?.GetValue<long>() ?? 0, usage?["output_tokens"]?.GetValue<long>() ?? 0, body["model"]?.GetValue<string>() ?? model);
    }
}

/// <summary>
/// OpenAI chat completions and everything that speaks the same dialect: OpenAI,
/// Azure OpenAI (deployment URL + api-key header) and Ollama's /v1 endpoint.
/// </summary>
public sealed class OpenAiCompatibleChatClient(HttpClient http, AiProvider provider, string model, string baseUrl, string? apiKey) : IChatClient
{
    public const string AzureApiVersion = "2024-10-21";

    public string Provider => provider.ToString();

    public string Model => model;

    public Uri Endpoint => provider == AiProvider.AzureOpenAI
        ? new Uri($"{baseUrl.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(model)}/chat/completions?api-version={AzureApiVersion}")
        : new Uri($"{baseUrl.TrimEnd('/')}/chat/completions");

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (provider == AiProvider.AzureOpenAI)
            {
                message.Headers.Add("api-key", apiKey);
            }
            else
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        }

        var payload = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = request.User }),
            ["temperature"] = request.Temperature,
        };
        if (provider != AiProvider.AzureOpenAI)
        {
            payload["model"] = model; // Azure takes the deployment from the URL
        }

        // Newer OpenAI models reject max_tokens; Ollama and Azure still expect it.
        payload[provider == AiProvider.OpenAI ? "max_completion_tokens" : "max_tokens"] = request.MaxTokens;

        message.Content = JsonContent.Create(payload);
        await message.Content.LoadIntoBufferAsync(cancellationToken);
        using var response = await http.SendAsync(message, cancellationToken);
        var body = await ChatHttp.ReadBodyOrThrowAsync(response, Provider, cancellationToken);

        var text = body["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var usage = body["usage"];
        return new ChatResult(text, usage?["prompt_tokens"]?.GetValue<long>() ?? 0, usage?["completion_tokens"]?.GetValue<long>() ?? 0, body["model"]?.GetValue<string>() ?? model);
    }
}

internal static class ChatHttp
{
    public static async Task<JsonNode> ReadBodyOrThrowAsync(HttpResponseMessage response, string provider, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ChatProviderException($"{provider} answered {(int)response.StatusCode} {response.ReasonPhrase}: {ErrorMessage(raw)}", (int)response.StatusCode);
        }

        try
        {
            return JsonNode.Parse(raw) ?? throw new ChatProviderException($"{provider} returned an empty body.");
        }
        catch (JsonException)
        {
            throw new ChatProviderException($"{provider} returned a non-JSON body.");
        }
    }

    private static string ErrorMessage(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            var message = node?["error"]?["message"]?.GetValue<string>() ?? node?["error"]?.ToString() ?? node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message.Length > 300 ? message[..300] : message;
            }
        }
        catch (JsonException)
        {
        }

        return raw.Length > 300 ? raw[..300] : raw;
    }
}

public sealed class ChatClientFactory(IHttpClientFactory httpClients) : IChatClientFactory
{
    public const string HttpClientName = "atlas-ai";

    public IChatClient Create(AiProviderSettings settings, string? apiKey)
    {
        var baseUrl = settings.BaseUrl ?? AiProviderSettings.DefaultBaseUrl(settings.Provider)
            ?? throw new InvalidOperationException($"{settings.Provider} needs a base URL.");
        if (settings.RequiresKey && string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException($"{settings.Provider} needs an API key.");
        }

        var http = httpClients.CreateClient(HttpClientName);
        return settings.Provider switch
        {
            AiProvider.Anthropic => new AnthropicChatClient(http, settings.Model, baseUrl, apiKey!),
            AiProvider.OpenAI or AiProvider.AzureOpenAI or AiProvider.Ollama => new OpenAiCompatibleChatClient(http, settings.Provider, settings.Model, baseUrl, apiKey),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Provider, "Unknown provider"),
        };
    }
}
