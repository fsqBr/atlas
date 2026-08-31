using Atlas.Connector.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Connector.Git;

public static class DependencyInjection
{
    /// <summary>Generic git connector, also exposed as IGitCloner for provider connectors.</summary>
    public static IServiceCollection AddGitConnector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection(GitConnectorOptions.SectionName).Get<GitConnectorOptions>() ?? new GitConnectorOptions());
        services.AddSingleton<GitHistoryReader>();
        services.AddSingleton<GitCliConnector>();
        services.AddSingleton<ISourceConnector>(sp => sp.GetRequiredService<GitCliConnector>());
        services.AddSingleton<IGitCloner>(sp => sp.GetRequiredService<GitCliConnector>());
        return services;
    }
}
