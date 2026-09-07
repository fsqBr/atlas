using Atlas.Domain.Findings;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Dependencies.Vulnerabilities;
using Atlas.Scanner.Runtime;

namespace Atlas.Scanner.Tests.Dependencies;

public class DependencyScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-depscanner").FullName;

    private static readonly ProjectFact Legacy = new(
        "src/Legacy/Legacy.csproj", "Legacy", IsSdkStyle: false, TargetFramework: "v4.5",
        PackageReferences: [new("EntityFramework", "6.1.3", PackageReferenceOrigin.PackagesConfig)],
        ProjectReferences: ["../Missing/Missing.csproj"],
        AssemblyReferences: ["System.Web"]);

    private static readonly ProjectFact Modern = new(
        "src/Modern/Modern.csproj", "Modern", IsSdkStyle: true, TargetFramework: "net10.0",
        PackageReferences: [], ProjectReferences: [], AssemblyReferences: []);

    private async Task<IReadOnlyList<FindingCandidate>> ScanAsync(params ProjectFact[] projects)
    {
        var scanner = new DependencyScanner(new NullVulnerabilitySource());
        var sink = new InMemoryFindingSink();
        var language = new LanguageAnalysisResult(
            "csharp", AnalysisTier.Syntactic, [], projects, [],
            new LanguageTotals(0, 0, 0, 0, 0, 0), null, [], [], [], []);

        var result = await scanner.ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(),
            ScanId = Guid.NewGuid(),
            RepositoryKey = "repo",
            Workspace = new ContainedArtifactReader(_root),
            Languages = new Dictionary<string, LanguageAnalysisResult> { ["csharp"] = language },
            Findings = sink,
            Today = new DateOnly(2026, 8, 28),
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        return sink.Candidates;
    }

    [Fact]
    public async Task Emits_only_declared_rules()
    {
        var candidates = await ScanAsync(Legacy, Modern);
        var declared = new DependencyScanner(new NullVulnerabilitySource()).Rules.Select(r => r.Id).ToHashSet();

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.Contains(c.RuleId, declared));
    }

    [Fact]
    public async Task Legacy_project_yields_eol_blockers_and_unresolved_reference()
    {
        var candidates = await ScanAsync(Legacy, Modern);
        var legacy = candidates.Where(c => c.Evidence.FilePath == Legacy.RelativePath).ToList();

        var eol = Assert.Single(legacy, c => c.RuleId == DependencyScanner.RuleIds.FrameworkEndOfLife);
        Assert.Equal(Severity.High, eol.Severity);
        Assert.Equal("v4.5", eol.Evidence.Symbol);

        var blockers = legacy.Where(c => c.RuleId.StartsWith(DependencyScanner.RuleIds.MigrationBlockerPrefix)).Select(c => c.Evidence.Symbol).ToList();
        Assert.Equal(["MB-001", "MB-002", "MB-003", "MB-006"], blockers.Order().ToList());

        var webForms = Assert.Single(legacy, c => c.Evidence.Symbol == "MB-003");
        Assert.Equal(Severity.High, webForms.Severity);
        Assert.NotNull(webForms.Remediation);

        Assert.Single(legacy, c => c.RuleId == DependencyScanner.RuleIds.UnresolvedProjectReference);
    }

    [Fact]
    public async Task Supported_modern_project_yields_nothing()
    {
        var candidates = await ScanAsync(Modern);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Candidates_never_carry_line_numbers_only_stable_symbols()
    {
        var candidates = await ScanAsync(Legacy);

        Assert.All(candidates, c =>
        {
            Assert.Null(c.Evidence.LineStart);
            Assert.False(string.IsNullOrWhiteSpace(c.Evidence.Symbol));
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
