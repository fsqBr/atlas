using System.Text.Json;
using Atlas.Application.Ai;

namespace Atlas.Ai;

public sealed class LocalAiOptions
{
    public const string SectionName = "Atlas:Ai";

    /// <summary>Ollama shipped with the deployment (compose profile ai-local / Helm ollama.enabled). Null = not configured.</summary>
    public string? LocalOllamaUrl { get; set; }

    /// <summary>Model the bundled Ollama pulls on start; the UI offers it with one click.</summary>
    public string LocalModel { get; set; } = "qwen2.5-coder:7b";
}

/// <summary>
/// Asks the bundled Ollama which models it has (GET /api/tags, 2 s budget). Lets the
/// Settings → AI page offer "Use local Ollama" only when it is actually running, and
/// pick a model that is really pulled.
/// </summary>
public sealed class LocalOllamaProbe(IHttpClientFactory httpClients, LocalAiOptions options) : ILocalAiProbe
{
    public const string HttpClientName = "atlas-ai-probe";

    public async Task<LocalAiStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.LocalOllamaUrl))
        {
            return new LocalAiStatus(null, false, [], options.LocalModel);
        }

        var url = options.LocalOllamaUrl.TrimEnd('/');
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var http = httpClients.CreateClient(HttpClientName);
            using var response = await http.GetAsync($"{url}/api/tags", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new LocalAiStatus(url, false, [], options.LocalModel);
            }

            var models = ParseModels(await response.Content.ReadAsStringAsync(timeout.Token));
            return new LocalAiStatus(url, true, models, options.LocalModel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new LocalAiStatus(url, false, [], options.LocalModel);
        }
    }

    public static IReadOnlyList<string> ParseModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return models.EnumerateArray()
                .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
