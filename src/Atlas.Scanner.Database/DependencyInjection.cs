using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Database;

public static class DependencyInjection
{
    public static IServiceCollection AddDatabaseScanner(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, DatabaseScanner>();
        return services;
    }
}
