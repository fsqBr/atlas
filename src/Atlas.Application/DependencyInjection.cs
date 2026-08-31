using Atlas.Application.Assessments;
using Atlas.Application.Credentials;
using Atlas.Connector.Abstractions;
using Atlas.Domain.Modernization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddAtlasApplication(this IServiceCollection services)
    {
        services.AddScoped<CreateAssessmentHandler>();
        services.AddScoped<DeleteAssessmentHandler>();
        services.AddScoped<RunAgainHandler>();
        services.AddScoped<RunComparisonBuilder>();
        services.AddScoped<AssessmentRunner>();
        services.AddScoped<IScanExecutor, InProcessScanExecutor>();
        services.AddScoped<Findings.TriageFindingHandler>();
        services.AddScoped<Findings.SuppressionPolicyHandler>();
        services.AddScoped<Findings.FindingViewsBuilder>();
        services.AddScoped<CredentialsService>();
        services.AddScoped<Atlas.Application.Security.ApiTokenService>();
        services.AddScoped<AssessmentAccessService>();
        services.AddScoped<Atlas.Application.Portfolio.SideBySideComparisonBuilder>();
        services.TryAddSingleton(new CostParameters());
        services.TryAddSingleton(new ScanLimits());
        services.AddScoped<ModernizationPlanBuilder>();
        services.AddScoped<CalibrationBuilder>();
        services.AddScoped<Portfolio.PortfolioBuilder>();
        services.AddSingleton<ICredentialProvider, ScopedCredentialProvider>();
        return services;
    }
}
