using Atlas.Language.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Language.VisualBasic;

public static class DependencyInjection
{
    public static IServiceCollection AddVisualBasicLanguage(this IServiceCollection services)
    {
        services.AddSingleton<ILanguageAnalyzer, VisualBasicLanguageAnalyzer>();
        return services;
    }
}
