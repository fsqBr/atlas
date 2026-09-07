using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Secrets;

public static class DependencyInjection
{
    public static IServiceCollection AddSecretsScanner(this IServiceCollection services, string? hmacKeyBase64)
    {
        services.AddSingleton(new SecretsScannerOptions { HmacKeyBase64 = hmacKeyBase64 });
        services.AddSingleton<IScanner, SecretsScanner>();
        return services;
    }
}
