using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Privacy;

public static class DependencyInjection
{
    public static IServiceCollection AddPrivacyScanner(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, PrivacyScanner>();
        return services;
    }
}
