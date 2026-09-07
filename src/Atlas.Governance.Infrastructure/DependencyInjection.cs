using Atlas.Application.AiEstate;
using Atlas.Governance.Application;
using Atlas.Governance.Infrastructure.Persistence;
using Atlas.Governance.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atlas.Governance.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// The governance module on the API host: its DbContext over the shared connection string,
    /// repositories, cost services and the read-only provider clients. Replaces the core's null cost port so
    /// the portfolio report gains the spend section.
    /// </summary>
    public static IServiceCollection AddAtlasGovernance(this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.AddDbContext<GovernanceDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(GovernanceDbContext.MigrationsHistoryTable, GovernanceDbContext.Schema))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IGovernanceUnitOfWork, EfGovernanceUnitOfWork>();
        services.AddScoped<ICostSourceRepository, CostSourceRepository>();
        services.AddScoped<ICostFactRepository, CostFactRepository>();
        services.AddScoped<ISeatFactRepository, SeatFactRepository>();
        services.AddScoped<CostSourceService>();
        services.AddScoped<CostSyncService>();
        services.Replace(ServiceDescriptor.Scoped<IAiCostSummarySource, AiCostSummaryBuilder>());

        services.AddSingleton(new GovernanceOptions { HmacKeyBase64 = configuration["Atlas:Secrets:HmacKeyBase64"] });
        services.AddHttpClient(ProviderHttp.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICostProviderClient, OpenAiCostClient>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICostProviderClient, AnthropicCostClient>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICostProviderClient, GitHubCopilotClient>());
        return services;
    }
}
