using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Security;

public static class DependencyInjection
{
    public static IServiceCollection AddSecurityScanner(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, SecurityScanner>();
        return services;
    }
}
