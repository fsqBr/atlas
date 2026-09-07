using System.Text;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Tests.Dependencies;

public class DependencyAnalyzerTests
{
    private static readonly DateOnly Today = new(2026, 8, 28);

    private static readonly ProjectFact Modern = new(
        RelativePath: Path.Combine("src", "Modern", "Modern.csproj"),
        Name: "Modern",
        IsSdkStyle: true,
        TargetFramework: "net8.0",
        PackageReferences: [new("Newtonsoft.Json", "13.0.3", PackageReferenceOrigin.PackageReference)],
        ProjectReferences: [@"..\Legacy\Legacy.csproj", @"..\Shared\Shared.csproj"],
        AssemblyReferences: []);

    private static readonly ProjectFact Legacy = new(
        RelativePath: Path.Combine("src", "Legacy", "Legacy.csproj"),
        Name: "Legacy",
        IsSdkStyle: false,
        TargetFramework: "v4.5",
        PackageReferences:
        [
            new("EntityFramework", "6.1.3", PackageReferenceOrigin.PackagesConfig),
            new("Newtonsoft.Json", "12.0.1", PackageReferenceOrigin.PackagesConfig),
        ],
        ProjectReferences: [@"..\Shared\Shared.csproj", @"..\Missing\Missing.csproj"],
        AssemblyReferences: ["System", "System.Web", "System.ServiceModel"]);

    private static readonly ProjectFact Shared = new(
        RelativePath: Path.Combine("src", "Shared", "Shared.csproj"),
        Name: "Shared",
        IsSdkStyle: true,
        TargetFramework: "netstandard2.0",
        PackageReferences: [],
        ProjectReferences: [],
        AssemblyReferences: []);

    private const string Bundle = """
        [
          {
            "id": "GHSA-5crp-9r3c-p9vr",
            "summary": "Newtonsoft.Json stack overflow on deeply nested JSON",
            "aliases": ["CVE-2024-21907"],
            "modified": "2024-06-01T00:00:00Z",
            "database_specific": { "severity": "HIGH" },
            "affected": [
              {
                "package": { "ecosystem": "NuGet", "name": "Newtonsoft.Json" },
                "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "13.0.1" } ] } ]
              }
            ]
          }
        ]
        """;

    private static async Task<DependencyAnalysisResult> AnalyzeAsync(IVulnerabilitySource? source = null)
    {
        source ??= new OsvJsonBundleVulnerabilitySource(new MemoryStream(Encoding.UTF8.GetBytes(Bundle)));
        return await new DependencyAnalyzer(source).AnalyzeAsync([Modern, Legacy, Shared], Today, CancellationToken.None);
    }

    [Fact]
    public async Task Detects_version_conflicts_across_projects()
    {
        var result = await AnalyzeAsync();

        var newtonsoft = Assert.Single(result.Packages, p => p.Id == "Newtonsoft.Json");
        Assert.True(newtonsoft.HasVersionConflict);
        Assert.Equal(["12.0.1", "13.0.3"], newtonsoft.Versions);
        Assert.Equal(2, newtonsoft.Projects.Count);
        Assert.True(newtonsoft.FromPackagesConfig);

        var ef = Assert.Single(result.Packages, p => p.Id == "EntityFramework");
        Assert.False(ef.HasVersionConflict);
    }

    [Fact]
    public async Task Builds_project_graph_with_fan_in_and_unresolved_edges()
    {
        var result = await AnalyzeAsync();

        var shared = Assert.Single(result.ProjectGraph.Nodes, n => n.Name == "Shared");
        Assert.Equal(2, shared.FanIn);
        Assert.Equal(0, shared.FanOut);

        var modern = Assert.Single(result.ProjectGraph.Nodes, n => n.Name == "Modern");
        Assert.Equal(2, modern.FanOut);

        Assert.Equal(4, result.ProjectGraph.Edges.Count);
        var unresolved = Assert.Single(result.ProjectGraph.Edges, e => !e.Resolved);
        Assert.Contains("Missing", unresolved.To);
    }

    [Fact]
    public async Task Classifies_framework_support()
    {
        var result = await AnalyzeAsync();

        Assert.Equal(FrameworkSupportStatus.EndOfLife,
            Assert.Single(result.Frameworks, f => f.ProjectPath == Legacy.RelativePath).Status);
        Assert.Equal(FrameworkSupportStatus.EndingSoon,
            Assert.Single(result.Frameworks, f => f.ProjectPath == Modern.RelativePath).Status);
        Assert.Equal(FrameworkSupportStatus.Supported,
            Assert.Single(result.Frameworks, f => f.ProjectPath == Shared.RelativePath).Status);
    }

    [Fact]
    public async Task Finds_migration_blockers_only_where_evidence_exists()
    {
        var result = await AnalyzeAsync();

        var legacyRules = result.MigrationBlockers
            .Where(b => b.ProjectPath == Legacy.RelativePath)
            .Select(b => b.RuleId)
            .OrderBy(r => r)
            .ToList();

        Assert.Equal(["MB-001", "MB-002", "MB-003", "MB-006", "MB-007"], legacyRules);
        Assert.DoesNotContain(result.MigrationBlockers, b => b.ProjectPath == Modern.RelativePath);
        Assert.DoesNotContain(result.MigrationBlockers, b => b.ProjectPath == Shared.RelativePath);

        var webForms = Assert.Single(result.MigrationBlockers, b => b.RuleId == "MB-003");
        Assert.Equal(BlockerImpact.High, webForms.Impact);
        Assert.Equal("Reference: System.Web", webForms.Evidence);
    }

    [Fact]
    public async Task Matches_vulnerable_versions_only()
    {
        var result = await AnalyzeAsync();

        var vuln = Assert.Single(result.Vulnerabilities);
        Assert.Equal("Newtonsoft.Json", vuln.PackageId);
        Assert.Equal("12.0.1", vuln.Version);
        Assert.Equal("GHSA-5crp-9r3c-p9vr", vuln.VulnerabilityId);
        Assert.Equal("13.0.1", vuln.FixedVersion);
        Assert.Equal("HIGH", vuln.Severity);
        Assert.Equal([Legacy.RelativePath], vuln.Projects);
        Assert.Contains("CVE-2024-21907", vuln.Aliases);
        Assert.StartsWith("osv:1 entries", result.Catalogs.VulnerabilityBundle);
    }

    [Fact]
    public async Task Runs_without_vulnerability_data_and_says_so()
    {
        var result = await AnalyzeAsync(new NullVulnerabilitySource());

        Assert.Empty(result.Vulnerabilities);
        Assert.Null(result.Catalogs.VulnerabilityBundle);
        Assert.Equal(FrameworkSupportCatalog.Version, result.Catalogs.FrameworkSupport);
        Assert.Equal(MigrationBlockerRules.Version, result.Catalogs.MigrationRules);
    }
}
