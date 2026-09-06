using Atlas.Language.Abstractions;
using Atlas.Language.Java;
using Atlas.Scanner.Runtime;

namespace Atlas.Language.Tests;

/// <summary>Tier 1 Java facts from a self-contained fixture: no compiler, no build, text as data.</summary>
public class JavaLanguageAnalyzerTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-java-fixture").FullName;
    private readonly JavaLanguageAnalyzer _analyzer = new();
    private LanguageAnalysisResult _result = null!;

    public async Task InitializeAsync()
    {
        Write("src/main/java/com/shop/App.java", """
            package com.shop;

            import java.security.MessageDigest;

            public class App {
                // a comment mentioning class Fake { }
                private String note = "class NotAType { }";

                public int busy(int n) {
                    int total = 0;
                    for (int i = 0; i < n; i++) {
                        if (i % 2 == 0 && i > 2 || i == 7) {
                            total += i;
                        } else if (i % 3 == 0) {
                            while (total > 100) { total -= 3; }
                        }
                        switch (i) {
                            case 1: total++; break;
                            case 2: total--; break;
                            case 3: total += 2; break;
                            case 4: total -= 2; break;
                        }
                        try { total += 1; } catch (Exception e) { total = 0; }
                    }
                    return total;
                }

                public byte[] weak(String s) throws Exception {
                    return MessageDigest.getInstance("MD5").digest(s.getBytes());
                }
            }
            """);
        Write("src/main/java/com/shop/Model.java", """
            package com.shop;

            enum Status { OPEN, DONE }
            interface Port { }
            record Point(int x, int y) { }
            """);
        Write("src/main/java/com/shop/Dao.java", """
            package com.shop;

            public class Dao {
                public Object load(java.sql.Statement stmt, String id) throws Exception {
                    return stmt.executeQuery("SELECT * FROM orders WHERE id = " + id);
                }
            }
            """);
        Write("src/test/java/com/shop/AppTest.java", """
            package com.shop;

            public class AppTest {
                @Test
                public void ok() { }
            }
            """);
        Write("target/generated-sources/Gen.java", "package gen; public class Generated { }");
        Write("src/main/java/com/shop/Spring.java", """
            package com.shop;

            import java.util.List;
            import java.util.Map;

            public class SpringishController {
                public SpringishController(int seed) { }

                @Override
                public String toString() { return "x"; }

                @Deprecated
                public Map<String, List<String>> convert(@SuppressWarnings("x") Long id, String name) {
                    if (id > 0 && name != null) { return Map.of(); }
                    return Map.of();
                }

                public Runnable job() {
                    return new Runnable() {
                        @Override
                        public void run() { }
                    };
                }
            }

            @interface Audit { }
            """);

        _result = await _analyzer.AnalyzeAsync(new ContainedArtifactReader(_root), CancellationToken.None);
    }

    [Fact]
    public void Emits_java_facts_at_the_syntactic_tier()
    {
        Assert.Equal("java", _result.LanguageId);
        Assert.Equal(AnalysisTier.Syntactic, _result.TierAchieved);
        Assert.Equal(5, _result.Totals.FileCount); // target/ build output is skipped
        Assert.Empty(_result.Projects); // manifests belong to the Java platform scanner
    }

    [Fact]
    public void Reads_types_with_package_and_kind_ignoring_comments_and_strings()
    {
        Assert.Contains(_result.Types, t => t is { Namespace: "com.shop", Name: "App", Kind: "class" });
        Assert.Contains(_result.Types, t => t is { Name: "Status", Kind: "enum" });
        Assert.Contains(_result.Types, t => t is { Name: "Port", Kind: "interface" });
        Assert.Contains(_result.Types, t => t is { Name: "Point", Kind: "record" });
        Assert.DoesNotContain(_result.Types, t => t.Name is "Fake" or "NotAType");
    }

    [Fact]
    public void Measures_complexity_and_reports_hot_methods()
    {
        var hot = Assert.Single(_result.HotMethods);
        Assert.Equal("App.busy", hot.Symbol);
        Assert.True(hot.CyclomaticComplexity >= 10, $"complexity was {hot.CyclomaticComplexity}");
        Assert.True(_result.Totals.MethodCount >= 4);
    }

    [Fact]
    public void Handles_spring_style_methods_constructors_and_annotation_types()
    {
        var file = Assert.Single(_result.Files, f => f.RelativePath.EndsWith("Spring.java", StringComparison.Ordinal));
        // toString (same-line after annotation lines), convert (nested generics + annotated
        // parameter), job, and the anonymous class's run — the constructor and the
        // "new Runnable() {" line itself are never methods.
        Assert.Equal(4, file.MethodCount);
        Assert.Contains(_result.Types, t => t is { Name: "Audit", Kind: "annotation" });
        Assert.DoesNotContain(_result.HotMethods, m => m.Symbol.Contains("SpringishController.SpringishController"));
    }

    [Fact]
    public void Counts_test_annotations()
    {
        var testFile = Assert.Single(_result.Files, f => f.RelativePath.EndsWith("AppTest.java", StringComparison.Ordinal));
        Assert.Equal(1, testFile.TestMethodCount);
    }

    [Fact]
    public void Detects_weak_hash_and_sql_concatenation_patterns()
    {
        var hash = Assert.Single(_result.Patterns, p => p.PatternId == SecurityPatternIds.WeakHash);
        Assert.Contains("MD5", hash.Detail);
        Assert.Equal("App", hash.Symbol);

        var sql = Assert.Single(_result.Patterns, p => p.PatternId == SecurityPatternIds.SqlStringConcatenation);
        Assert.EndsWith("Dao.java", sql.FilePath);
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
