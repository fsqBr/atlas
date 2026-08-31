using Atlas.Language.Abstractions;
using Atlas.Language.Python;
using Atlas.Scanner.Runtime;

namespace Atlas.Language.Tests;

/// <summary>Tier 1 Python facts from a self-contained fixture: no interpreter, text as data.</summary>
public class PythonLanguageAnalyzerTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-python-fixture").FullName;
    private readonly PythonLanguageAnalyzer _analyzer = new();
    private LanguageAnalysisResult _result = null!;

    public async Task InitializeAsync()
    {
        Write("app.py", """
            import hashlib
            import sqlite3


            class OrderRepository:
                def find(self, conn, order_id):
                    cur = conn.cursor()
                    return cur.execute("SELECT * FROM orders WHERE id = " + order_id)

                def token(self, seed):
                    return hashlib.md5(seed.encode()).hexdigest()


            def classify(a, b, c):
                score = 0
                for i in range(a):
                    if i % 2 == 0 and i > b or i == c:
                        score += 2
                    elif i % 3 == 0:
                        while score > 40:
                            score -= 7
                    if score < 0:
                        score = 0
                    try:
                        score += b
                    except ValueError:
                        score = c
                    if i == 7 or i == 9:
                        score += 1
                return score
            """);
        Write("edge.py", """
            '''Module docstring with def fake(x): and class NotAType:'''
            import asyncio

            BANNER = "class AlsoNotAType: def also_fake(self):"  # comment with class Ghost:


            class Outer:
                class Inner:
                    def ping(self):
                        return "ok"

                async def fetch(self, url):
                    return await asyncio.sleep(0)


            def top(x):
                return x if x > 0 else 0
            """);
        Write("tests/test_app.py", """
            def test_alpha():
                assert True


            def test_beta():
                assert True
            """);
        Write("libs/site-packages/vendor.py", "class Vendored:\n    pass\n");
        Write("broken.py", "s = \"never closed...\nx = 1\n");
        Write("black.py", """
            def merge(
                left,
                right,
                limit,
            ):
                if left and right or limit:
                    for item in left:
                        if item > limit:
                            right.append(item)
                return right
            """);

        _result = await _analyzer.AnalyzeAsync(new ContainedArtifactReader(_root), CancellationToken.None);
    }

    [Fact]
    public void Emits_python_facts_at_the_syntactic_tier()
    {
        Assert.Equal("python", _result.LanguageId);
        Assert.Equal(AnalysisTier.Syntactic, _result.TierAchieved);
        Assert.Equal(5, _result.Totals.FileCount); // site-packages is vendored, never analyzed
        Assert.Empty(_result.Projects); // manifests belong to the Python platform scanner
    }

    [Fact]
    public void Black_style_multiline_signatures_still_reach_the_body()
    {
        // The "):" line at the def's own indent must not terminate the body scan: the real
        // decisions live below it (before the fix this file scored complexity 1).
        var black = Assert.Single(_result.Files, f => f.RelativePath.EndsWith("black.py", StringComparison.Ordinal));
        Assert.Equal(1, black.MethodCount);
        Assert.Equal(6, black.MaxCyclomaticComplexity); // 1 + if + and + or + for + if
    }

    [Fact]
    public void Reads_classes_ignoring_docstrings_strings_and_comments()
    {
        Assert.Contains(_result.Types, t => t is { Name: "OrderRepository", Kind: "class" });
        Assert.Contains(_result.Types, t => t.Name == "Outer");
        Assert.Contains(_result.Types, t => t.Name == "Inner");
        Assert.DoesNotContain(_result.Types, t => t.Name is "NotAType" or "AlsoNotAType" or "Ghost" or "Vendored");
    }

    [Fact]
    public void Measures_complexity_with_class_ownership_and_reports_hot_functions()
    {
        var hot = Assert.Single(_result.HotMethods);
        Assert.Equal("app.classify", hot.Symbol); // module-level function owned by the file stem
        Assert.True(hot.CyclomaticComplexity >= 10, $"complexity was {hot.CyclomaticComplexity}");

        var edge = Assert.Single(_result.Files, f => f.RelativePath.EndsWith("edge.py", StringComparison.Ordinal));
        Assert.Equal(3, edge.MethodCount); // ping (Inner), fetch (Outer, async), top (module)
    }

    [Fact]
    public void Counts_test_functions()
    {
        var testFile = Assert.Single(_result.Files, f => f.RelativePath.EndsWith("test_app.py", StringComparison.Ordinal));
        Assert.Equal(2, testFile.TestMethodCount);
    }

    [Fact]
    public void Detects_weak_hash_and_sql_concatenation_patterns()
    {
        var hash = Assert.Single(_result.Patterns, p => p.PatternId == SecurityPatternIds.WeakHash);
        Assert.Contains("md5", hash.Detail);
        Assert.Equal("OrderRepository", hash.Symbol);

        var sql = Assert.Single(_result.Patterns, p => p.PatternId == SecurityPatternIds.SqlStringConcatenation);
        Assert.EndsWith("app.py", sql.FilePath);
    }

    [Fact]
    public void Unterminated_strings_never_break_the_scan()
    {
        Assert.Contains(_result.Files, f => f.RelativePath.EndsWith("broken.py", StringComparison.Ordinal));
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
