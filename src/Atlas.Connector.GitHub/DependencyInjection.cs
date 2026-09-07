using Atlas.Connector.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Connector.GitHub;

public static class DependencyInjection
{
    /// <summary>Requires AddGitConnector() (IGitCloner) to be registered as well.</summary>
    public static IServiceCollection AddGitHubConnector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection(GitHubConnectorOptions.SectionName).Get<GitHubConnectorOptions>() ?? new GitHubConnectorOptions());
        services.AddHttpClient(GitHubConnector.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<ISourceConnector, GitHubConnector>();
        return services;
    }
}
