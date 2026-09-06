using Atlas.Application.Assessments;
using Atlas.Application.Audit;
using Atlas.Application.Credentials;
using Atlas.Application.Findings;
using Atlas.Infrastructure.Security;
using Atlas.Application.Workspaces;
using Atlas.Infrastructure.Jobs;
using Atlas.Infrastructure.Persistence;
using Atlas.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAtlasInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        services.AddDbContext<AtlasDbContext>(options => options
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "atlas"))
            .UseSnakeCaseNamingConvention());

        services.Configure<WorkspaceManagerOptions>(
            configuration.GetSection(WorkspaceManagerOptions.SectionName));
        services.AddScoped<IWorkspaceManager, WorkspaceManager>();

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<Atlas.Application.Tenants.ITenantRepository, TenantRepository>();
        services.AddScoped<Atlas.Application.Security.IApiTokenRepository, ApiTokenRepository>();
        services.AddScoped<IAssessmentAccessRepository, AssessmentAccessRepository>();
        services.AddScoped<IAssessmentRepository, AssessmentRepository>();
        services.AddScoped<IAssessmentRunRepository, AssessmentRunRepository>();
        services.AddScoped<IScanRepository, ScanRepository>();
        services.AddScoped<IFindingRepository, FindingRepository>();
        services.AddScoped<ISuppressionRepository, SuppressionRepository>();
        services.AddScoped<ISuppressionPolicyRepository, SuppressionPolicyRepository>();
        services.AddScoped<IModernizationActualRepository, ModernizationActualRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<Atlas.Application.Ai.IAiSettingsRepository, AiSettingsRepository>();
        services.AddScoped<Atlas.Application.Ai.IBusinessRuleRepository, BusinessRuleRepository>();
        services.AddScoped<Atlas.Application.Ai.IAiNarrativeRepository, AiNarrativeRepository>();
        services.AddScoped<IRuleCatalog, RuleCatalog>();
        services.AddScoped<IRuleOverrideRepository, RuleOverrideRepository>();
        services.AddScoped<ITenantCostProfileRepository, TenantCostProfileRepository>();
        services.AddScoped<ITenantNotificationSettingsRepository, TenantNotificationSettingsRepository>();
        services.AddScoped<ISystemMarkerRepository, SystemMarkerRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IHealthRepository, HealthRepository>();
        services.AddScoped<IScanJobQueue, PostgresScanJobQueue>();

        // Encrypted connector credentials (AES-GCM under Atlas:Secrets:MasterKeyBase64).
        services.AddSingleton(configuration.GetSection(SecretCipherOptions.SectionName).Get<SecretCipherOptions>() ?? new SecretCipherOptions());
        services.AddSingleton<ISecretCipher, AesGcmSecretCipher>();
        services.AddScoped<ICredentialRepository, ConnectorCredentialRepository>();

        return services;
    }
}
