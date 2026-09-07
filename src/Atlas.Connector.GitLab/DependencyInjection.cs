using Atlas.Connector.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Connector.GitLab;

public static class DependencyInjection
{
    /// <summary>Requires AddGitConnector() (IGitCloner) to be registered as well.</summary>
    public static IServiceCollection AddGitLabConnector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection(GitLabConnectorOptions.SectionName).Get<GitLabConnectorOptions>() ?? new GitLabConnectorOptions());
        services.AddHttpClient(GitLabConnector.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<ISourceConnector, GitLabConnector>();
        return services;
    }
}
