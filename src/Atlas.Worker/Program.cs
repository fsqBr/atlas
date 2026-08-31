using Atlas.Application;
using Atlas.Application.Assessments;
using Atlas.Connector.Abstractions;
using Atlas.Connector.AzureDevOps;
using Atlas.Connector.Git;
using Atlas.Connector.GitHub;
using Atlas.Connector.GitLab;
using Atlas.Ai;
using Atlas.Application.Ai;
using Atlas.Connector.Upload;
using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Language.Sql;
using Atlas.Connector.Local;
using Atlas.Infrastructure;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Secrets;
using Atlas.Scanner.Security;
using Atlas.Worker;

if (ScanHost.IsScanHostInvocation(args))
{
    return await ScanHost.RunAsync(args);
}

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("AtlasDb")
    ?? throw new InvalidOperationException(
        "Connection string 'AtlasDb' is not configured. Set ConnectionStrings__AtlasDb via environment.");

builder.Services.AddAtlasInfrastructure(builder.Configuration, connectionString);
builder.Services.AddAtlasApplication();
builder.Services.AddAtlasScanning(builder.Configuration);

// in compose every run executes in a disposable child process (Atlas__Scanning__Isolation=ChildProcess).
var scanning = builder.Configuration.GetSection(ScanningOptions.SectionName).Get<ScanningOptions>() ?? new ScanningOptions();
builder.Services.AddSingleton(scanning);
builder.Services.AddSingleton(new ScanLimits { ScannerTimeoutMinutes = scanning.ScannerTimeoutMinutes, MaxFiles = scanning.MaxFiles });
if (builder.Configuration.GetValue<bool>("Atlas:Operations:JsonLogs"))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(json => { json.UseUtcTimestamp = true; json.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
}
if (scanning.UseChildProcess)
{
    builder.Services.AddScoped<IScanExecutor, ChildProcessScanExecutor>();
}

builder.Services.AddSingleton<ISourceConnector, LocalFolderConnector>();
builder.Services.AddGitConnector(builder.Configuration);
builder.Services.AddGitHubConnector(builder.Configuration);
builder.Services.AddAzureDevOpsConnector(builder.Configuration);
builder.Services.AddGitLabConnector(builder.Configuration);
builder.Services.AddUploadConnector(builder.Configuration);
builder.Services.AddAtlasAi(builder.Configuration);
builder.Services.AddSingleton<Atlas.Application.Tenants.ITenantContext>(Atlas.Application.Tenants.SystemTenantContext.Instance);
builder.Services.AddSqlLanguage();
builder.Services.AddSingleton<BusinessRuleCandidateSource>();
builder.Services.AddSingleton<IBusinessRuleCandidateSource>(sp => new CompositeBusinessRuleCandidateSource([sp.GetRequiredService<BusinessRuleCandidateSource>(), sp.GetRequiredService<Atlas.Language.Sql.SqlBusinessRuleCandidateSource>()]));

builder.Services.AddSingleton(builder.Configuration.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>() ?? new NotificationOptions());
builder.Services.AddHttpClient(RunNotifier.HttpClientName);
builder.Services.AddSingleton<RunNotifier>();
builder.Services.AddHostedService<ScanJobWorker>();
builder.Services.AddHostedService<WorkspaceGcService>();
builder.Services.AddHostedService<WeeklyDigestService>();

var host = builder.Build();
host.Run();
return 0;
