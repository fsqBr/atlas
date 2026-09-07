using Atlas.Application.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Ai;

public static class DependencyInjection
{
    /// <summary>AI provider clients + settings service + rule extractor. The worker adds the Roslyn candidate source separately.</summary>
    public static IServiceCollection AddAtlasAi(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddSingleton(configuration?.GetSection(LocalAiOptions.SectionName).Get<LocalAiOptions>() ?? new LocalAiOptions());
        services.AddHttpClient(LocalOllamaProbe.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(3));
        services.AddSingleton<ILocalAiProbe, LocalOllamaProbe>();
        services.AddHttpClient(ChatClientFactory.HttpClientName, http =>
        {
            http.Timeout = TimeSpan.FromMinutes(3);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas/1.0 (+https://github.com/atlas)");
        });
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddScoped<AiSettingsService>();
        services.AddSingleton<BusinessRuleExtractor>();
        services.AddScoped<QueueBusinessRuleAnalysisHandler>();
        services.AddScoped<BusinessRuleAnalysisRunner>();
        services.AddScoped<QueueFindingFixHandler>();
        services.AddScoped<FindingFixRunner>();
        services.AddScoped<AiNarrativeService>();
        services.AddScoped<AiFeedbackService>();
        return services;
    }
}
