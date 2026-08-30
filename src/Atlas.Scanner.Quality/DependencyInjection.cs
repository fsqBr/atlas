using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Quality;

public static class DependencyInjection
{
    public static IServiceCollection AddQualityScanner(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, QualityScanner>();
        return services;
    }
}
