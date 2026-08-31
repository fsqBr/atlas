using NetArchTest.Rules;

namespace Atlas.ArchitectureTests;

/// <summary>
/// Executable architecture rules. These run in CI:
/// a boundary violation is a build break, not a code-review opinion.
/// </summary>
public class LayerDependencyTests
{
    private static readonly System.Reflection.Assembly Domain = typeof(Atlas.Domain.Tenants.Tenant).Assembly;
    private static readonly System.Reflection.Assembly Application = typeof(Atlas.Application.AssemblyMarker).Assembly;
    private static readonly System.Reflection.Assembly Contracts = typeof(Atlas.Contracts.AssemblyMarker).Assembly;
    private static readonly System.Reflection.Assembly ScannerAbstractions = typeof(Atlas.Scanner.Abstractions.IScanner).Assembly;

    [Fact]
    public void Domain_does_not_depend_on_outer_layers_or_frameworks()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Atlas.Application",
                "Atlas.Infrastructure",
                "Atlas.Contracts",
                "Atlas.Api",
                "Atlas.Worker",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql")
            .GetResult();

        Assert.True(result.IsSuccessful, FailureList(result));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_hosts()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Atlas.Infrastructure",
                "Atlas.Api",
                "Atlas.Worker",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql")
            .GetResult();

        Assert.True(result.IsSuccessful, FailureList(result));
    }

    [Fact]
    public void Contracts_do_not_depend_on_any_atlas_assembly()
    {
        var result = Types.InAssembly(Contracts)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Atlas.Domain",
                "Atlas.Application",
                "Atlas.Infrastructure",
                "Atlas.Api",
                "Atlas.Worker")
            .GetResult();

        Assert.True(result.IsSuccessful, FailureList(result));
    }

    [Fact]
    public void Scanner_abstractions_do_not_depend_on_infrastructure_or_hosts()
    {
        var result = Types.InAssembly(ScannerAbstractions)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Atlas.Infrastructure",
                "Atlas.Api",
                "Atlas.Worker",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful, FailureList(result));
    }

    [Fact]
    public void Connector_abstractions_depend_only_on_domain()
    {
        var result = Types.InAssembly(typeof(Atlas.Connector.Abstractions.ISourceConnector).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Atlas.Application",
                "Atlas.Infrastructure",
                "Atlas.Api",
                "Atlas.Worker",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful, FailureList(result));
    }

    [Fact]
    public void Connector_implementations_do_not_leak_into_core()
    {
        var connectors = new[]
        {
            typeof(Atlas.Connector.Local.LocalFolderConnector).Assembly,
            typeof(Atlas.Connector.Git.GitCliConnector).Assembly,
        };

        foreach (var assembly in connectors)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "Atlas.Application",
                    "Atlas.Infrastructure",
                    "Atlas.Api",
                    "Atlas.Worker",
                    "Microsoft.EntityFrameworkCore",
                    "Microsoft.AspNetCore")
                .GetResult();

            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name}: {FailureList(result)}");
        }
    }

    [Fact]
    public void Language_assemblies_do_not_depend_on_infrastructure_or_hosts()
    {
        var languageAssemblies = new[]
        {
            typeof(Atlas.Language.Abstractions.ILanguageAnalyzer).Assembly,
            typeof(Atlas.Language.CSharp.CSharpLanguageAnalyzer).Assembly,
        };

        foreach (var assembly in languageAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "Atlas.Application",
                    "Atlas.Infrastructure",
                    "Atlas.Api",
                    "Atlas.Worker",
                    "Atlas.Connector",
                    "Microsoft.EntityFrameworkCore",
                    "Microsoft.AspNetCore")
                .GetResult();

            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name}: {FailureList(result)}");
        }
    }

    [Fact]
    public void Scanners_depend_on_language_abstractions_only_never_a_concrete_language_or_the_core()
    {
        var scannerAssemblies = new[]
        {
            typeof(Atlas.Scanner.Dependencies.DependencyAnalyzer).Assembly,
            typeof(Atlas.Scanner.Secrets.SecretsScanner).Assembly,
            typeof(Atlas.Scanner.Security.SecurityScanner).Assembly,
            typeof(Atlas.Scanner.Quality.QualityScanner).Assembly,
            typeof(Atlas.Scanner.Architecture.ArchitectureScanner).Assembly,
        };

        foreach (var assembly in scannerAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(
                    "Atlas.Language.CSharp",
                    "Atlas.Application",
                    "Atlas.Infrastructure",
                    "Atlas.Api",
                    "Atlas.Worker",
                    "Atlas.Connector",
                    "Microsoft.CodeAnalysis",
                    "Microsoft.EntityFrameworkCore",
                    "Microsoft.AspNetCore")
                .GetResult();

            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name}: {FailureList(result)}");
        }
    }

    [Fact]
    public void Reporting_depends_on_application_ports_only()
    {
        var result = Types.InAssembly(typeof(Atlas.Reporting.ExecutiveReportBuilder).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Atlas.Infrastructure",
                "Atlas.Api",
                "Atlas.Worker",
                "Atlas.Language.CSharp",
                "Atlas.Scanner.Dependencies",
                "Atlas.Scanner.Secrets",
                "Atlas.Scanner.Security",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful, FailureList(result));
    }

    private static string FailureList(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violations: " + string.Join(", ", result.FailingTypes?.Select(t => t.FullName ?? "?") ?? []);
}
