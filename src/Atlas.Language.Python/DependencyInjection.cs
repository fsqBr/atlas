using Atlas.Language.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Language.Python;

public static class DependencyInjection
{
    public static IServiceCollection AddPythonLanguage(this IServiceCollection services)
    {
        services.AddSingleton<ILanguageAnalyzer, PythonLanguageAnalyzer>();
        return services;
    }
}
