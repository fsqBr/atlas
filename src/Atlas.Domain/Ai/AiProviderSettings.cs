namespace Atlas.Domain.Ai;

/// <summary>
/// Tenant-level configuration of the LLM used for AI-assisted analysis
///. Bring-your-own key: the secret is stored as an AES-GCM envelope
/// under the master key, never returned by the API, and nothing is sent to a
/// provider unless <see cref="Enabled"/> is true.
/// </summary>
public sealed class AiProviderSettings
{
    public const int MaxModelLength = 200;
    public const int MaxBaseUrlLength = 500;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public AiProvider Provider { get; private set; }
    public string Model { get; private set; } = null!;
    public string? BaseUrl { get; private set; }
    public byte[]? KeyEnvelope { get; private set; }
    public bool Enabled { get; private set; }
    public int MaxSnippetsPerAnalysis { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? LastTestedAtUtc { get; private set; }
    public bool? LastTestSucceeded { get; private set; }
    public string? LastTestMessage { get; private set; }

    private AiProviderSettings()
    {
    }

    public AiProviderSettings(Guid id, Guid tenantId)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Ids must not be empty.");
        }

        Id = id;
        TenantId = tenantId;
        Provider = AiProvider.Anthropic;
        Model = DefaultModel(Provider);
        MaxSnippetsPerAnalysis = 40;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public bool HasKey => KeyEnvelope is { Length: > 0 };

    /// <summary>Ollama runs locally and needs no key; every hosted provider does.</summary>
    public bool RequiresKey => RequiresKeyFor(Provider);

    public static bool RequiresKeyFor(AiProvider provider) => provider != AiProvider.Ollama;

    public bool IsUsable => Enabled && (!RequiresKey || HasKey);

    public void Configure(AiProvider provider, string? model, string? baseUrl, byte[]? keyEnvelope, bool enabled, int? maxSnippets)
    {
        model = string.IsNullOrWhiteSpace(model) ? DefaultModel(provider) : model.Trim();
        if (model.Length > MaxModelLength)
        {
            throw new ArgumentException($"Model name must be at most {MaxModelLength} characters.", nameof(model));
        }

        baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/');
        if (baseUrl is not null)
        {
            if (baseUrl.Length > MaxBaseUrlLength || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new ArgumentException("Base URL must be an absolute http(s) URL.", nameof(baseUrl));
            }
        }

        if (provider == AiProvider.AzureOpenAI && baseUrl is null)
        {
            throw new ArgumentException("Azure OpenAI needs the resource endpoint as base URL (https://<resource>.openai.azure.com).", nameof(baseUrl));
        }

        if (maxSnippets is { } m && (m < 1 || m > 500))
        {
            throw new ArgumentException("Snippets per analysis must be between 1 and 500.", nameof(maxSnippets));
        }

        var providerChanged = provider != Provider;
        Provider = provider;
        Model = model;
        BaseUrl = baseUrl;
        if (keyEnvelope is { Length: > 0 })
        {
            KeyEnvelope = keyEnvelope;
        }
        else if (providerChanged)
        {
            // A key belongs to one provider; switching without a new key must not reuse the old one.
            KeyEnvelope = null;
        }

        Enabled = enabled;
        MaxSnippetsPerAnalysis = maxSnippets ?? MaxSnippetsPerAnalysis;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        LastTestedAtUtc = null;
        LastTestSucceeded = null;
        LastTestMessage = null;
    }

    public void ClearKey()
    {
        KeyEnvelope = null;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void RecordTest(bool succeeded, string message)
    {
        LastTestedAtUtc = DateTimeOffset.UtcNow;
        LastTestSucceeded = succeeded;
        LastTestMessage = message.Length > 500 ? message[..500] : message;
    }

    public static string DefaultModel(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => "claude-sonnet-5",
        AiProvider.OpenAI => "gpt-4.1-mini",
        AiProvider.AzureOpenAI => "gpt-4.1-mini",
        AiProvider.Ollama => "llama3.1",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static string? DefaultBaseUrl(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => "https://api.anthropic.com",
        AiProvider.OpenAI => "https://api.openai.com/v1",
        AiProvider.AzureOpenAI => null,
        AiProvider.Ollama => "http://host.docker.internal:11434/v1",
        _ => null,
    };
}

public enum AiProvider
{
    Anthropic = 0,
    OpenAI = 1,
    AzureOpenAI = 2,
    Ollama = 3,
}
