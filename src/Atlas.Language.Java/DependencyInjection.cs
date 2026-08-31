using Atlas.Language.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Language.Java;

public static class DependencyInjection
{
    public static IServiceCollection AddJavaLanguage(this IServiceCollection services)
    {
        services.AddSingleton<ILanguageAnalyzer, JavaLanguageAnalyzer>();
        return services;
    }
}
