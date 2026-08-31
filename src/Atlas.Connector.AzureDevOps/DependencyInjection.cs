using Atlas.Connector.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Connector.AzureDevOps;

public static class DependencyInjection
{
    /// <summary>Requires AddGitConnector() (IGitCloner) to be registered as well.</summary>
    public static IServiceCollection AddAzureDevOpsConnector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection(AzureDevOpsConnectorOptions.SectionName).Get<AzureDevOpsConnectorOptions>() ?? new AzureDevOpsConnectorOptions());
        services.AddHttpClient(AzureDevOpsConnector.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<ISourceConnector, AzureDevOpsConnector>();
        return services;
    }
}
