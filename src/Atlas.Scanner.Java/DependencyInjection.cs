using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Java;

public static class DependencyInjection
{
    /// <summary>Requires an IVulnerabilitySource registration (AddDependencyScanner provides it).</summary>
    public static IServiceCollection AddJavaScanner(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, JavaScanner>();
        return services;
    }
}
