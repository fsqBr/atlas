using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Dependencies.Vulnerabilities;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Dependencies;

public static class DependencyInjection
{
    /// <param name="osvBundlePath">Path to an OSV JSON bundle; null runs without vulnerability data (reported as such).</param>
    public static IServiceCollection AddDependencyScanner(this IServiceCollection services, string? osvBundlePath)
    {
        if (string.IsNullOrWhiteSpace(osvBundlePath))
        {
            services.AddSingleton<IVulnerabilitySource, NullVulnerabilitySource>();
        }
        else
        {
            // The file may not exist yet (first sync pending) and changes over time: reload on demand.
            services.AddSingleton<IVulnerabilitySource>(_ => new ReloadingVulnerabilitySource(osvBundlePath));
        }

        services.AddSingleton<IScanner, DependencyScanner>();
        return services;
    }
}
