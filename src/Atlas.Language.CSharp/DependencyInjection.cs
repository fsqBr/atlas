using Atlas.Language.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Language.CSharp;

public static class DependencyInjection
{
    public static IServiceCollection AddCSharpLanguage(this IServiceCollection services, Tier2Options? tier2 = null)
    {
        services.AddSingleton(tier2 ?? new Tier2Options());
        services.AddSingleton<RestoredReferences>();
        services.AddSingleton<ILanguageAnalyzer, CSharpLanguageAnalyzer>();
        return services;
    }
}
