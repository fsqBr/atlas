using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Python;

public static class DependencyInjection
{
    /// <summary>Requires an IVulnerabilitySource registration (AddDependencyScanner provides it).</summary>
    public static IServiceCollection AddPythonScanner(this IServiceCollection services)
    {
        services.AddSingleton<IScanner, PythonScanner>();
        return services;
    }
}
