using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Quality;

namespace Atlas.Scanner.Tests.Quality;

public class DuplicationAndChurnTests
{
    private const string Block = """
        public decimal Total(Order order)
        {
            var subtotal = 0m;
            foreach (var line in order.Lines)
            {
                if (line.Quantity <= 0) continue;
                subtotal += line.Quantity * line.UnitPrice;
                if (line.Discount > 0) subtotal -= line.Discount;
            }
            var tax = subtotal * order.TaxRate;
            var shipping = order.Weight > 10 ? 25m : 10m;
            var total = subtotal + tax + shipping;
            if (order.Coupon is not null) total -= order.Coupon.Value;
            if (total < 0) total = 0;
            order.Total = total;
            return total;
        }
        """;

    [Fact]
    public void Detects_copy_pasted_blocks_across_files_and_ignores_formatting()
    {
        var a = "using System;\nnamespace A\n{\n    public class Billing\n    {\n" + Block + "\n    }\n}\n";
        var b = "// copied\nnamespace B {\n  public class Invoicing {\n" + Block.Replace("    ", "\t") + "\n  }\n}\n";
        var c = "namespace C { public class Unrelated { public int X() => 1; } }";

        var blocks = DuplicationDetector.Detect(new Dictionary<string, string> { ["A/Billing.cs"] = a, ["B/Invoicing.cs"] = b, ["C/U.cs"] = c });

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, bl => Assert.True(bl.Lines >= DuplicationDetector.MinBlockLines, bl.Lines.ToString()));
        var inA = blocks.Single(bl => bl.FilePath == "A/Billing.cs");
        Assert.StartsWith("B/Invoicing.cs:", inA.OtherLocations.Single());
        Assert.Equal(blocks[0].Hash, blocks[1].Hash);
    }

    [Fact]
    public void Short_or_unique_code_is_not_reported()
    {
        var blocks = DuplicationDetector.Detect(new Dictionary<string, string>
        {
            ["a.cs"] = "class A { int X => 1; int Y => 2; int Z => 3; }",
            ["b.cs"] = "class B { int X => 1; int Y => 2; int Z => 3; }",
        });
        Assert.Empty(blocks);
    }

    private sealed class Sink : IFindingSink
    {
        public List<FindingCandidate> Items { get; } = [];

        public void Emit(FindingCandidate candidate) => Items.Add(candidate);
    }

    private sealed class EmptyReader : IArtifactReader
    {
        public string RootPath => "/none";

        public IEnumerable<string> EnumerateFiles(string searchPattern) => [];

        public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Stream OpenRead(string relativePath) => Stream.Null;
    }

    [Fact]
    public async Task Architecture_scanner_turns_history_into_change_hotspots_and_knowledge_silos()
    {
        var files = new List<FileFact>
        {
            new("src/Core/OrderService.cs", 900, 3, 40, 32, false, 0),
            new("src/Core/Simple.cs", 40, 1, 2, 2, false, 0),
            new("src/Core/Lonely.cs", 300, 2, 10, 6, false, 0),
        };
        var history = new List<FileChangeFact>
        {
            new("src/Core/OrderService.cs", 24, 900, 400, 5, DateTimeOffset.UtcNow),
            new("src/Core/Simple.cs", 30, 60, 20, 4, DateTimeOffset.UtcNow),   // churn without complexity: not a hotspot
            new("src/Core/Lonely.cs", 12, 200, 50, 1, DateTimeOffset.UtcNow),   // one author: knowledge silo
            new("README.md", 40, 100, 100, 6, DateTimeOffset.UtcNow),           // not a source file
        };
        var sink = new Sink();
        var context = new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "r", Workspace = new EmptyReader(),
            Languages = new Dictionary<string, LanguageAnalysisResult>
            {
                ["csharp"] = new("csharp", AnalysisTier.Syntactic, [], [], files, new LanguageTotals(3, 1240, 6, 52, 28, 8), null, [], [], [], []),
            },
            Findings = sink, Today = new DateOnly(2026, 8, 29), History = history,
        };

        await new ArchitectureScanner().ExecuteAsync(context, CancellationToken.None);

        var hotspot = Assert.Single(sink.Items, c => c.RuleId == ArchitectureScanner.RuleIds.ChangeHotspot);
        Assert.Equal("src/Core/OrderService.cs", hotspot.Evidence.FilePath);
        Assert.Equal("24", hotspot.Data!["commits"]);
        Assert.Equal(Severity.High, hotspot.Severity);

        var silo = Assert.Single(sink.Items, c => c.RuleId == ArchitectureScanner.RuleIds.KnowledgeSilo);
        Assert.Equal("src/Core/Lonely.cs", silo.Evidence.FilePath);
    }

    [Fact]
    public async Task Without_history_no_change_rules_fire()
    {
        var sink = new Sink();
        var context = new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "r", Workspace = new EmptyReader(),
            Languages = new Dictionary<string, LanguageAnalysisResult>
            {
                ["csharp"] = new("csharp", AnalysisTier.Syntactic, [], [], [new FileFact("a.cs", 900, 3, 40, 28, false, 0)], new LanguageTotals(1, 900, 3, 40, 28, 8), null, [], [], [], []),
            },
            Findings = sink, Today = new DateOnly(2026, 8, 29),
        };
        await new ArchitectureScanner().ExecuteAsync(context, CancellationToken.None);
        Assert.DoesNotContain(sink.Items, c => c.RuleId is ArchitectureScanner.RuleIds.ChangeHotspot or ArchitectureScanner.RuleIds.KnowledgeSilo);
    }
}
