namespace Atlas.Governance.Application;

/// <summary>Best-effort provider from a model id, for budgets by provider and reconciliation with billing. Unknown → null.</summary>
public static class ModelProviders
{
    private static readonly (string Prefix, string Provider)[] Prefixes =
    [
        ("claude", "anthropic"),
        ("gpt-", "openai"), ("o1", "openai"), ("o3", "openai"), ("o4", "openai"), ("chatgpt", "openai"), ("text-embedding", "openai"), ("codex", "openai"),
        ("gemini", "google"),
        ("deepseek", "deepseek"),
        ("mistral", "mistral"), ("codestral", "mistral"), ("mixtral", "mistral"), ("ministral", "mistral"),
        ("llama", "meta"),
        ("grok", "xai"),
        ("command", "cohere"),
        ("amazon.", "aws-bedrock"), ("anthropic.", "aws-bedrock"), ("meta.", "aws-bedrock"),
    ];

    public static string? Guess(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var m = model.Trim().ToLowerInvariant();
        foreach (var (prefix, provider) in Prefixes)
        {
            if (m.StartsWith(prefix, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>Normalises provider ids coming from telemetry (gen_ai.provider.name / gen_ai.system) to Atlas ids.</summary>
    public static string? Normalize(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "openai" or "azure.ai.openai" or "azure_openai" or "azure-openai" => provider.Trim().ToLowerInvariant().Contains("azure") ? "azure-openai" : "openai",
        "anthropic" => "anthropic",
        "gcp.gen_ai" or "gcp.vertex_ai" or "gcp.gemini" or "google" or "vertex_ai" or "gemini" => "google",
        "aws.bedrock" or "bedrock" => "aws-bedrock",
        "mistral_ai" or "mistral" => "mistral",
        "deepseek" => "deepseek",
        "xai" or "x_ai" => "xai",
        "cohere" => "cohere",
        var other => other.Length <= 40 ? other : other[..40],
    };
}
