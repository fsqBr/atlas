using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureScanner(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, InfrastructureScanner>();
        return services;
    }
}
