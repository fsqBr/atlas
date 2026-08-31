using Atlas.Domain.Findings;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Runtime;

namespace Atlas.Scanner.Tests.Quality;

public class DeadCodeEmitTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-deadcode").FullName;

    private async Task<IReadOnlyList<FindingCandidate>> ScanAsync(params PatternFact[] patterns)
    {
        var language = new LanguageAnalysisResult(
            "csharp", AnalysisTier.DesignTime, [], [], [], new LanguageTotals(0, 0, 0, 0, 0, 0), null, patterns, [], [], []);
        var sink = new InMemoryFindingSink();
        var result = await new QualityScanner().ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "repo",
            Workspace = new ContainedArtifactReader(_root),
            Languages = new Dictionary<string, LanguageAnalysisResult> { ["csharp"] = language },
            Findings = sink, Today = new DateOnly(2026, 8, 30),
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        return sink.Candidates;
    }

    [Fact]
    public async Task Dead_type_patterns_become_informational_candidates_deduped_per_type()
    {
        var candidates = await ScanAsync(
            new PatternFact(QualityPatternIds.DeadType, "src/App/Orphan.cs", 12, "Orphan", "internal class 'App.Orphan' has no source references in the analyzed code"),
            new PatternFact(QualityPatternIds.DeadType, "src/App/Orphan.cs", 40, "Orphan", "internal class 'App.Orphan' has no source references in the analyzed code"),
            new PatternFact(QualityPatternIds.ObsoleteApi, "src/App/Other.cs", 3, "Other.Run", "X is [Obsolete]"));

        var dead = Assert.Single(candidates, c => c.RuleId == QualityScanner.RuleIds.DeadCode);
        Assert.Equal(Severity.Informational, dead.Severity);
        Assert.Equal(ConfidenceLevel.Medium, dead.Confidence);
        Assert.Equal("Dead code candidate: Orphan in Orphan.cs", dead.Title);
        Assert.Contains("cannot be ruled out statically", dead.Message);
        Assert.Equal("src/App/Orphan.cs", dead.Evidence.FilePath);
        Assert.Equal(12, dead.Evidence.LineStart);
        Assert.Equal("Orphan", dead.Data!["typeName"]);
    }

    [Fact]
    public async Task No_dead_type_patterns_means_no_dead_code_findings()
    {
        var candidates = await ScanAsync(
            new PatternFact(QualityPatternIds.LegacyApi, "src/App/Legacy.cs", 5, "Legacy.Run", "new WebClient(): use HttpClient"));

        Assert.DoesNotContain(candidates, c => c.RuleId == QualityScanner.RuleIds.DeadCode);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
