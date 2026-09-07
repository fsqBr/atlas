using Atlas.Domain.Findings;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Runtime;

namespace Atlas.Scanner.Tests.Quality;

public class QualityScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-quality").FullName;

    private static ProjectFact Project(string path, string name, string[] refs, string[] packages) => new(
        path, name, IsSdkStyle: true, TargetFramework: "net8.0",
        PackageReferences: packages.Select(p => new PackageReferenceFact(p, "1.0.0", PackageReferenceOrigin.PackageReference)).ToList(),
        ProjectReferences: refs, AssemblyReferences: []);

    private static readonly ProjectFact App = Project("src/App/App.csproj", "App", [], []);
    private static readonly ProjectFact Core = Project("src/Core/Core.csproj", "Core", [], []);
    private static readonly ProjectFact AppTests = Project("tests/App.Tests/App.Tests.csproj", "App.Tests", ["../../src/App/App.csproj"], ["xunit"]);

    private async Task<IReadOnlyList<FindingCandidate>> ScanAsync(ProjectFact[] projects, FileFact[]? files = null, MethodFact[]? hot = null)
    {
        var language = new LanguageAnalysisResult(
            "csharp", AnalysisTier.Syntactic, [], projects, files ?? [], new LanguageTotals(0, 0, 0, 0, 0, 0), null, [], hot ?? [], [], []);
        var sink = new InMemoryFindingSink();
        var result = await new QualityScanner().ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "repo",
            Workspace = new ContainedArtifactReader(_root),
            Languages = new Dictionary<string, LanguageAnalysisResult> { ["csharp"] = language },
            Findings = sink, Today = new DateOnly(2026, 8, 28),
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        return sink.Candidates;
    }

    [Fact]
    public async Task Production_project_without_test_reference_is_flagged_but_covered_one_is_not()
    {
        var candidates = await ScanAsync([App, Core, AppTests]);

        var uncovered = Assert.Single(candidates, c => c.RuleId == QualityScanner.RuleIds.ProjectUncovered);
        Assert.Equal(Core.RelativePath, uncovered.Evidence.FilePath);
        Assert.DoesNotContain(candidates, c => c.RuleId == QualityScanner.RuleIds.NoTests);
    }

    [Fact]
    public async Task No_tests_at_all_is_one_estate_level_finding()
    {
        var candidates = await ScanAsync([App, Core]);

        Assert.Single(candidates, c => c.RuleId == QualityScanner.RuleIds.NoTests);
        Assert.DoesNotContain(candidates, c => c.RuleId == QualityScanner.RuleIds.ProjectUncovered);
    }

    [Fact]
    public async Task Missing_coverage_is_reported_as_unknown_not_zero()
    {
        var candidates = await ScanAsync([App, AppTests]);

        var noData = Assert.Single(candidates, c => c.RuleId == QualityScanner.RuleIds.CoverageNoData);
        Assert.Equal(Severity.Informational, noData.Severity);
        Assert.Contains("not zero", noData.Message);
    }

    [Fact]
    public async Task Ingests_cobertura_report_and_flags_low_packages()
    {
        File.WriteAllText(Path.Combine(_root, "coverage.cobertura.xml"), """
            <?xml version="1.0"?>
            <coverage line-rate="0.61" branch-rate="0.5" version="1.9">
              <packages>
                <package name="App" line-rate="0.82" />
                <package name="Core" line-rate="0.31" />
              </packages>
            </coverage>
            """);

        var candidates = await ScanAsync([App, Core, AppTests]);

        var summary = Assert.Single(candidates, c => c.RuleId == QualityScanner.RuleIds.CoverageSummary);
        Assert.Contains("61.0%", summary.Title);
        var low = Assert.Single(candidates, c => c.RuleId == QualityScanner.RuleIds.CoverageLow);
        Assert.Equal("Core", low.Evidence.Symbol);
        Assert.DoesNotContain(candidates, c => c.RuleId == QualityScanner.RuleIds.CoverageNoData);
    }

    [Fact]
    public async Task Complexity_large_files_and_syntax_errors_map_to_rules()
    {
        var files = new[]
        {
            new FileFact("src/App/Big.cs", 1500, 3, 40, 12, false, 0),
            new FileFact("src/App/Broken.cs", 10, 0, 0, 0, true, 0),
        };
        var hot = new[]
        {
            new MethodFact("src/App/Big.cs", "Big.Medium", 10, 18, 60),
            new MethodFact("src/App/Big.cs", "Big.Severe", 200, 35, 300),
            new MethodFact("src/App/Big.cs", "Big.Warm", 400, 12, 20), // below the reporting threshold
        };

        var candidates = await ScanAsync([App, AppTests], files, hot);

        Assert.Single(candidates, c => c.RuleId == QualityScanner.RuleIds.LargeFile);
        Assert.Single(candidates, c => c.RuleId == QualityScanner.RuleIds.SyntaxError);
        var complex = candidates.Where(c => c.RuleId == QualityScanner.RuleIds.ComplexMethod).ToList();
        Assert.Equal(2, complex.Count);
        Assert.Equal(Severity.High, complex.Single(c => c.Evidence.Symbol == "Big.Severe").Severity);
        Assert.Equal(Severity.Medium, complex.Single(c => c.Evidence.Symbol == "Big.Medium").Severity);
    }

    [Fact]
    public void Test_project_detection_uses_packages_assemblies_and_names()
    {
        Assert.True(QualityScanner.IsTestProject(AppTests));
        Assert.True(QualityScanner.IsTestProject(Project("t/X.csproj", "X", [], ["NUnit"])));
        Assert.True(QualityScanner.IsTestProject(new ProjectFact("t/Y.csproj", "Y", false, "v4.5", [], [], ["nunit.framework"])));
        Assert.True(QualityScanner.IsTestProject(Project("t/Z.UnitTests.csproj", "Z.UnitTests", [], [])));
        Assert.False(QualityScanner.IsTestProject(App));
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
