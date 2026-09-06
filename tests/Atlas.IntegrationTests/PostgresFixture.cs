using Atlas.Application;
using Atlas.Connector.Abstractions;
using Atlas.Connector.Local;
using Atlas.Infrastructure;
using Atlas.Infrastructure.Persistence;
using Atlas.Language.CSharp;
using Atlas.Language.VisualBasic;
using Atlas.Reporting;
using Atlas.Scanner.Database;
using Atlas.Scanner.JavaScript;
using Atlas.Scanner.Licenses;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Infrastructure;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Privacy;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Secrets;
using Atlas.Scanner.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace Atlas.IntegrationTests;

/// <summary>
/// Real PostgreSQL in a container, schema applied by the real EF migrations
/// (never EnsureCreated — the design notes). Services wired exactly as the hosts do.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("atlas-it-ws").FullName;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var provider = BuildServices();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Database.MigrateAsync();
    }

    public ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Atlas:Workspaces:RootPath"] = WorkspaceRoot,
                ["Atlas:Secrets:MasterKeyBase64"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAtlasInfrastructure(configuration, _container.GetConnectionString());
        services.AddAtlasApplication();
        services.AddSingleton<Atlas.Application.Tenants.ITenantContext>(Atlas.Application.Tenants.SystemTenantContext.Instance);
        services.AddScannerRuntime();
        services.AddCSharpLanguage();
        services.AddVisualBasicLanguage();
        services.AddDependencyScanner(osvBundlePath: null);
        services.AddSecretsScanner(Convert.ToBase64String(new byte[32]));
        services.AddSecurityScanner();
        services.AddQualityScanner();
        services.AddPrivacyScanner();
        services.AddDatabaseScanner();
        services.AddJavaScriptScanner();
        services.AddLicenseScanner(new LicenseOptions { Enabled = false });
        services.AddInfrastructureScanner();
        services.AddArchitectureScanner();
        services.AddAtlasReporting(new ReportOptions { BrandName = "Atlas Test", PreparedBy = "CI" });
        services.AddSingleton<ISourceConnector, LocalFolderConnector>();

        return services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        try
        {
            Directory.Delete(WorkspaceRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
