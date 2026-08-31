using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Architecture;

public static class DependencyInjection
{
    public static IServiceCollection AddArchitectureScanner(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, ArchitectureScanner>();
        return services;
    }
}
