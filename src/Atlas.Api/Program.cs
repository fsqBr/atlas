using Atlas.Api;
using Atlas.Api.Endpoints;
using Atlas.Application;
using Atlas.Application.Assessments;
using Atlas.Application.Credentials;
using Atlas.Application.Findings;
using Atlas.Connector.Abstractions;
using Atlas.Connector.AzureDevOps;
using Atlas.Connector.Git;
using Atlas.Connector.GitHub;
using Atlas.Connector.GitLab;
using Atlas.Ai;
using Atlas.Application.Ai;
using Atlas.Application.Security;
using Atlas.Application.Tenants;
using Atlas.Connector.Upload;
using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Language.Sql;
using Atlas.Language.VisualBasic;
using Atlas.Connector.Local;
using Atlas.Contracts.Assessments;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;
using Atlas.Governance.Infrastructure;
using Atlas.Infrastructure;
using Atlas.Infrastructure.Persistence;
using Atlas.Reporting;
using Atlas.Scanner.Ai;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Database;
using Atlas.Scanner.JavaScript;
using Atlas.Scanner.Licenses;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Infrastructure;
using Atlas.Scanner.Privacy;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Secrets;
using Atlas.Scanner.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1024L * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 1024L * 1024 * 1024);

var connectionString = builder.Configuration.GetConnectionString("AtlasDb")
    ?? throw new InvalidOperationException(
        "Connection string 'AtlasDb' is not configured. " +
        "Set ConnectionStrings__AtlasDb via environment or user secrets — never in appsettings.json for real environments.");

builder.Services.AddAtlasInfrastructure(builder.Configuration, connectionString);
builder.Services.AddAtlasGovernance(builder.Configuration, connectionString);
builder.Services.AddSingleton(builder.Configuration.GetSection(CostSyncScheduleOptions.SectionName).Get<CostSyncScheduleOptions>() ?? new CostSyncScheduleOptions());
builder.Services.AddSingleton<CostSyncScheduleState>();
builder.Services.AddHostedService<CostSyncSchedulerService>();
builder.Services.AddSingleton(builder.Configuration.GetSection(Atlas.Domain.Modernization.CostParameters.SectionName).Get<Atlas.Domain.Modernization.CostParameters>() ?? new Atlas.Domain.Modernization.CostParameters());
builder.Services.AddSingleton(builder.Configuration.GetSection(LocalSourcesOptions.SectionName).Get<LocalSourcesOptions>() ?? new LocalSourcesOptions());
builder.Services.AddAtlasApplication();
builder.Services.AddScannerRuntime();
builder.Services.AddCSharpLanguage();
builder.Services.AddVisualBasicLanguage();
builder.Services.AddSqlLanguage();
builder.Services.AddDependencyScanner(builder.Configuration["Atlas:Vulnerabilities:OsvBundlePath"]);

var feedOptions = builder.Configuration.GetSection(VulnerabilityFeedOptions.SectionName).Get<VulnerabilityFeedOptions>() ?? new VulnerabilityFeedOptions();
builder.Services.AddSingleton(feedOptions);
builder.Services.AddHttpClient("osv");
builder.Services.AddHostedService<VulnerabilityFeedSyncService>();
builder.Services.AddHostedService<ScheduledRunsService>();
builder.Services.AddHostedService<UploadGcService>();
builder.Services.AddHostedService<SuppressionExpiryService>();
builder.Services.AddScoped<IssueExportService>();
builder.Services.AddScoped<Atlas.Application.Assessments.DemoSeeder>();
builder.Services.AddSecretsScanner(builder.Configuration["Atlas:Secrets:HmacKeyBase64"]);
builder.Services.AddSecurityScanner();
builder.Services.AddQualityScanner();
builder.Services.AddPrivacyScanner();
builder.Services.AddDatabaseScanner();
builder.Services.AddJavaScriptScanner();
builder.Services.AddLicenseScanner(builder.Configuration.GetSection(LicenseOptions.SectionName).Get<LicenseOptions>());
builder.Services.AddInfrastructureScanner();
builder.Services.AddArchitectureScanner();
builder.Services.AddAiEstateScanner(builder.Configuration.GetSection(AiEstateOptions.SectionName).Get<AiEstateOptions>(), builder.Configuration["Atlas:Secrets:HmacKeyBase64"]);
builder.Services.AddAtlasReporting(
    builder.Configuration.GetSection(ReportOptions.SectionName).Get<ReportOptions>() ?? new ReportOptions());

builder.Services.AddSingleton<ISourceConnector, LocalFolderConnector>();
builder.Services.AddGitConnector(builder.Configuration);
builder.Services.AddGitHubConnector(builder.Configuration);
builder.Services.AddAzureDevOpsConnector(builder.Configuration);
builder.Services.AddGitLabConnector(builder.Configuration);
builder.Services.AddUploadConnector(builder.Configuration);
builder.Services.AddAtlasAi(builder.Configuration);
builder.Services.AddSingleton<BusinessRuleCandidateSource>();
builder.Services.AddSingleton<IBusinessRuleCandidateSource>(sp => new CompositeBusinessRuleCandidateSource([sp.GetRequiredService<BusinessRuleCandidateSource>(), sp.GetRequiredService<Atlas.Language.Sql.SqlBusinessRuleCandidateSource>()]));

builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

var authOptions = builder.AddAtlasAuth();
var tenantOptions = builder.AddAtlasTenants();
var operationsOptions = builder.AddAtlasOperations();

var app = builder.Build();
app.UseAtlasAuth(authOptions);
app.UseAtlasTenants(tenantOptions, authOptions);
// Viewers of a restricted assessment cannot change it: any non-GET under /api/assessments/{id}/… needs edit rights
// (the sharing endpoints check ownership themselves). Hidden assessments are already 404 through the query filter.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var method = context.Request.Method;
    if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method)
        && path.StartsWith("/api/assessments/", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("/access", StringComparison.OrdinalIgnoreCase))
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3 && Guid.TryParse(segments[2], out var assessmentId))
        {
            var tenant = context.RequestServices.GetRequiredService<ITenantContext>();
            if (!tenant.IsAdmin)
            {
                var access = context.RequestServices.GetRequiredService<AssessmentAccessService>();
                if (!await access.CanEditAsync(assessmentId, context.RequestAborted))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "You have view-only access to this assessment." });
                    return;
                }
            }
        }
    }

    await next(context);
});

app.UseAtlasOperations(operationsOptions);

// Opt-in schema migration for local/self-hosted single-node deployments (Atlas__AutoMigrate=true).
// Clustered deployments must run migrations as an explicit deploy step instead.
if (app.Configuration.GetValue<bool>("Atlas:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<HttpTenantContext>().UseSystemScope();
    scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Database.Migrate();
    scope.ServiceProvider.GetRequiredService<Atlas.Governance.Infrastructure.Persistence.GovernanceDbContext>().Database.Migrate();
}

app.MapGet("/", () => Results.Ok(new
{
    name = "Atlas API",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
}));

// Public: what the SPA needs to start the OIDC flow (or to learn that auth is off).
app.MapGet("/api/auth/config", (AuthOptions auth) => Results.Ok(auth.ToPublicConfig()));

// The running version, shown in the UI footer so an install can be matched to a release tag at a glance.
app.MapGet("/api/version", () => Results.Ok(new
{
    version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
}));

// SourceEndpoints: see Endpoints/SourceEndpoints.cs
SourceEndpoints.Map(app);

// CredentialEndpoints: see Endpoints/CredentialEndpoints.cs
CredentialEndpoints.Map(app);

// PortfolioEndpoints: see Endpoints/PortfolioEndpoints.cs
PortfolioEndpoints.Map(app);

// JobEndpoints: see Endpoints/JobEndpoints.cs
JobEndpoints.Map(app);

var assessments = app.MapGroup("/api/assessments").RequireRateLimiting("api");

// Everything under /api/assessments lives in Endpoints/: lifecycle, findings, gate, runs, reports and sharing;
// interop (SBOM, exports, SARIF import, compliance bundle); AI narratives; waivers and suppression policies.
AssessmentEndpoints.Map(assessments);
AssessmentInteropEndpoints.Map(assessments);
AiNarrativeEndpoints.Map(assessments);

// Waivers and suppression policies (tenant-wide + assessment-scoped) live in Endpoints/SuppressionEndpoints.cs.
SuppressionEndpoints.Map(app, assessments);

// TenantEndpoints: see Endpoints/TenantEndpoints.cs
TenantEndpoints.Map(app);

// TokenEndpoints: see Endpoints/TokenEndpoints.cs
TokenEndpoints.Map(app);

// Rule catalog and tenant severity tuning live in Endpoints/RuleEndpoints.cs.
RuleEndpoints.Map(app);

// AI Estate / governance module endpoints live in Endpoints/AiEstateEndpoints.cs.
AiEstateEndpoints.Map(app);

// SettingsEndpoints: see Endpoints/SettingsEndpoints.cs
SettingsEndpoints.Map(app);

// AiSettingsEndpoints: see Endpoints/AiSettingsEndpoints.cs
AiSettingsEndpoints.Map(app);

// Liveness: process is up. Readiness: dependencies (Postgres) reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

/// <summary>Exposes the entry point to in-process integration tests (WebApplicationFactory).</summary>
public partial class Program;
