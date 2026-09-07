using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Scanner.Runtime;

public static class DependencyInjection
{
    public static IServiceCollection AddScannerRuntime(this IServiceCollection services)
    {
        services.AddSingleton<IArtifactReaderFactory, ContainedArtifactReaderFactory>();
        return services;
    }
}
