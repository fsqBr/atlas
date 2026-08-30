using Atlas.Connector.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Connector.Upload;

public static class DependencyInjection
{
    public static IServiceCollection AddUploadConnector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration.GetSection(UploadOptions.SectionName).Get<UploadOptions>() ?? new UploadOptions());
        services.AddSingleton<UploadConnector>();
        services.AddSingleton<ISourceConnector>(sp => sp.GetRequiredService<UploadConnector>());
        return services;
    }
}
