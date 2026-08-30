using Atlas.Domain.Findings;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Runtime;

namespace Atlas.Scanner.Tests.Architecture;

public class ArchitectureScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-arch").FullName;

    private static ProjectFact Project(string path, string name, params string[] refs) => new(
        path, name, true, "net8.0", [], refs, []);

    private async Task<IReadOnlyList<FindingCandidate>> ScanAsync(
        ProjectFact[] projects, NamespaceDependency[] edges, TypeFact[]? types = null, MethodFact[]? hot = null)
    {
        var language = new LanguageAnalysisResult(
            "csharp", AnalysisTier.SyntacticWithSymbols, [], projects, [], new LanguageTotals(0, 0, 0, 0, 0, 0), null, [], hot ?? [], types ?? [], edges);
        var sink = new InMemoryFindingSink();
        var result = await new ArchitectureScanner().ExecuteAsync(new ScanContext
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
    public async Task Detects_project_cycles_only()
    {
        var projects = new[]
        {
            Project("src/A/A.csproj", "A", "../B/B.csproj"),
            Project("src/B/B.csproj", "B", "../A/A.csproj"),
            Project("src/C/C.csproj", "C", "../A/A.csproj"),
        };

        var candidates = await ScanAsync(projects, []);

        var cycle = Assert.Single(candidates, c => c.RuleId == ArchitectureScanner.RuleIds.ProjectCycle);
        Assert.Equal(Severity.High, cycle.Severity);
        Assert.Contains("A", cycle.Title);
        Assert.Contains("B", cycle.Title);
        Assert.DoesNotContain("C", cycle.Evidence.Symbol!);
    }

    [Fact]
    public async Task Detects_namespace_cycles_fan_out_and_hotspots()
    {
        var edges = new List<NamespaceDependency>
        {
            new("N1", "N2", 3), new("N2", "N3", 1), new("N3", "N1", 2), // cycle N1→N2→N3→N1
            new("N4", "N1", 1), new("N5", "N1", 1), new("N6", "N1", 1), // N1 fan-in = N3,N4,N5,N6
        };
        for (var i = 0; i < 12; i++)
        {
            edges.Add(new("Hub", $"Leaf{i}", 1)); // fan-out 12
        }

        var types = new[] { new TypeFact("src/N1/Core.cs", "N1", "Core", "class") };
        var hot = new[]
        {
            new MethodFact("src/N1/Core.cs", "Core.A", 1, 20, 50),
            new MethodFact("src/N1/Core.cs", "Core.B", 60, 25, 50),
            new MethodFact("src/N1/Core.cs", "Core.C", 120, 12, 50),
        };

        var candidates = await ScanAsync([], edges.ToArray(), types, hot);

        var cycle = Assert.Single(candidates, c => c.RuleId == ArchitectureScanner.RuleIds.NamespaceCycle);
        Assert.Equal("N1|N2|N3", cycle.Evidence.Symbol);

        var fanOut = Assert.Single(candidates, c => c.RuleId == ArchitectureScanner.RuleIds.HighFanOut);
        Assert.Equal("Hub", fanOut.Evidence.Symbol);

        var hotspot = Assert.Single(candidates, c => c.RuleId == ArchitectureScanner.RuleIds.Hotspot);
        Assert.Equal("N1", hotspot.Evidence.Symbol);
        Assert.Contains("4 namespaces depend", hotspot.Message);
    }

    [Fact]
    public void Tarjan_finds_all_components()
    {
        var components = StronglyConnectedComponents.Find(
            ["a", "b", "c", "d", "e"],
            [("a", "b"), ("b", "a"), ("b", "c"), ("c", "d"), ("d", "c"), ("e", "a")]);

        var sizes = components.Select(c => c.Count).OrderBy(n => n).ToList();
        Assert.Equal([1, 2, 2], sizes);
        Assert.Contains(components, c => c.Order().SequenceEqual(["a", "b"]));
        Assert.Contains(components, c => c.Order().SequenceEqual(["c", "d"]));
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
