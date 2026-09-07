using Atlas.Application.AiEstate;
using Atlas.Governance.Application;
using Atlas.Governance.Infrastructure.Persistence;
using Atlas.Governance.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Custom price list wins wholesale; anything wrong with it falls back to the builtin with a warning.</summary>
    internal static PriceCatalog LoadPrices(string? path, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var custom = PriceCatalog.LoadFile(path);
                logger.LogInformation("AI price catalog loaded from {Path}: version {Version}.", path, custom.Version);
                return custom;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
            {
                logger.LogWarning(ex, "AI price catalog at {Path} could not be loaded; using the builtin catalog.", path);
            }
        }

        return PriceCatalog.LoadBuiltin();
    }

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
        services.AddScoped<IUsageFactRepository, UsageFactRepository>();
        services.AddScoped<IUsageEventRepository, UsageEventRepository>();
        services.AddScoped<UsageReportService>();
        services.AddScoped<IModelPriceRepository, ModelPriceRepository>();
        services.AddScoped<IPriceCatalogResolver, PriceCatalogResolver>();
        services.AddScoped<ModelPriceService>();
        services.AddSingleton(sp => LoadPrices(configuration["Atlas:AiEstate:PriceCatalogPath"], sp.GetRequiredService<ILoggerFactory>().CreateLogger("Atlas.Governance.Prices")));
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
