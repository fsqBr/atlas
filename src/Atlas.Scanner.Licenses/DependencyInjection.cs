using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Licenses;

public static class DependencyInjection
{
    public static IServiceCollection AddLicenseScanner(this IServiceCollection services, LicenseOptions? options = null)
    {
        services.AddSingleton(options ?? new LicenseOptions());
        services.AddHttpClient(RegistryLicenseResolver.HttpClientName, http =>
        {
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas/1.0 (+license-compliance)");
        });
        services.AddSingleton<ILicenseResolver, RegistryLicenseResolver>();
        services.AddSingleton<IScanner, LicenseScanner>();
        return services;
    }
}
