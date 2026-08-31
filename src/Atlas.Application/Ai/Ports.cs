using Atlas.Domain.Ai;
using Atlas.Domain.Workspaces;

namespace Atlas.Application.Ai;

public interface IAiSettingsRepository
{
    Task<AiProviderSettings?> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(AiProviderSettings settings);
}

public interface IBusinessRuleRepository
{
    Task<IReadOnlyList<BusinessRule>> ListAsync(Guid assessmentId, CancellationToken cancellationToken);

    /// <summary>Rules of an assessment are replaced as a whole by each completed analysis.</summary>
    Task ReplaceAsync(Guid assessmentId, IReadOnlyList<BusinessRule> rules, CancellationToken cancellationToken);

    void AddAnalysis(BusinessRuleAnalysis analysis);

    Task<IReadOnlyList<BusinessRuleAnalysis>> ListAnalysesAsync(Guid assessmentId, int take, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, int>> CountByAssessmentAsync(IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken);

    Task<BusinessRule?> GetAsync(Guid ruleId, CancellationToken cancellationToken);

    /// <summary>Rules somebody voted on, newest vote first (quality signal for the AI settings page).</summary>
    Task<IReadOnlyList<BusinessRule>> ListRatedAsync(int take, CancellationToken cancellationToken);
}

public interface IAiNarrativeRepository
{
    Task<AiNarrative?> GetAsync(Guid assessmentId, string kind, string key, string lang, CancellationToken cancellationToken);

    Task<IReadOnlyList<AiNarrative>> ListAsync(Guid assessmentId, string kind, string lang, CancellationToken cancellationToken);

    /// <summary>Narratives somebody voted on, newest vote first.</summary>
    Task<IReadOnlyList<AiNarrative>> ListRatedAsync(int take, CancellationToken cancellationToken);

    void Add(AiNarrative narrative);
}

/// <summary>Bundled local model runtime (Ollama shipped with the deployment): is it up, which models are pulled.</summary>
public sealed record LocalAiStatus(string? Url, bool Available, IReadOnlyList<string> Models, string DefaultModel);

public interface ILocalAiProbe
{
    Task<LocalAiStatus> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>One chat completion: system + user prompt in, text out. Provider-neutral.</summary>
public interface IChatClient
{
    string Provider { get; }

    string Model { get; }

    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}

public sealed record ChatRequest(string System, string User, int MaxTokens = 4096, double Temperature = 0.1);

public sealed record ChatResult(string Text, long InputTokens, long OutputTokens, string Model);

public interface IChatClientFactory
{
    IChatClient Create(AiProviderSettings settings, string? apiKey);
}

/// <summary>Thrown when the provider answered with an error status; message is safe to show (no key).</summary>
public sealed class ChatProviderException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
