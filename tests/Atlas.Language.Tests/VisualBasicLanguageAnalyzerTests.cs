using System.Text;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Language.VisualBasic;

namespace Atlas.Language.Tests;

public class VisualBasicLanguageAnalyzerTests
{
    private sealed class MemoryReader(Dictionary<string, string> files) : IArtifactReader
    {
        public string RootPath => "/mem";

        public IEnumerable<string> EnumerateFiles(string searchPattern)
        {
            var suffix = searchPattern.TrimStart('*');
            return files.Keys.Where(k => searchPattern == "*" || k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(k).Equals(searchPattern, StringComparison.OrdinalIgnoreCase));
        }

        public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(files[relativePath]);

        public Stream OpenRead(string relativePath) => new MemoryStream(Encoding.UTF8.GetBytes(files[relativePath]));
    }

    private const string Pricing = """
        Imports System.Data.SqlClient
        Imports System.Security.Cryptography

        Namespace Shop.Billing
            Public Class PricingService
                Public Function Discount(ByVal total As Decimal, ByVal items As Integer, ByVal vip As Boolean) As Decimal
                    If vip AndAlso total > 1000 Then
                        Return total * 0.15D
                    ElseIf items > 10 Then
                        Return total * 0.05D
                    End If
                    Select Case items
                        Case 1
                            Return 0
                        Case 2, 3
                            Return 1
                    End Select
                    For i As Integer = 0 To items
                        If i Mod 2 = 0 Then total += 1
                        If i Mod 3 = 0 Then total += 2
                        If i Mod 5 = 0 Then total += 3
                    Next
                    Return If(total > 0, 0D, 1D)
                End Function

                Public Sub Load(ByVal name As String)
                    Dim cmd As New SqlCommand("SELECT * FROM Customers WHERE Name = '" & name & "'")
                    Dim hash = New MD5CryptoServiceProvider()
                End Sub
            End Class

            Public Module Helpers
                <Fact>
                Public Sub Discount_is_positive()
                End Sub
            End Module
        End Namespace
        """;

    [Fact]
    public async Task Produces_files_types_hot_methods_tests_and_security_patterns()
    {
        var files = new Dictionary<string, string>
        {
            ["src/Shop/PricingService.vb"] = Pricing,
            ["src/Shop/Shop.vbproj"] = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>""",
        };
        var analyzer = new VisualBasicLanguageAnalyzer();
        var reader = new MemoryReader(files);

        Assert.True(analyzer.CanAnalyze(reader));
        var result = await analyzer.AnalyzeAsync(reader, CancellationToken.None);

        Assert.Equal("vb", result.LanguageId);
        Assert.Equal(AnalysisTier.Syntactic, result.TierAchieved);
        Assert.Single(result.Projects);
        Assert.Equal("Shop", result.Projects[0].Name);
        Assert.Equal("net48", result.Projects[0].TargetFramework);

        var file = Assert.Single(result.Files);
        Assert.Equal(2, file.TypeCount);
        Assert.Equal(3, file.MethodCount);
        Assert.Equal(1, file.TestMethodCount);
        Assert.False(file.HasSyntaxErrors);

        Assert.Contains(result.Types, t => t.Name == "PricingService" && t.Kind == "class" && t.Namespace == "Shop.Billing");
        Assert.Contains(result.Types, t => t.Name == "Helpers" && t.Kind == "module");

        var hot = Assert.Single(result.HotMethods);
        Assert.Equal("PricingService.Discount", hot.Symbol);
        Assert.True(hot.CyclomaticComplexity >= 10, $"complexity {hot.CyclomaticComplexity}");

        Assert.Contains(result.Patterns, p => p.PatternId == SecurityPatternIds.SqlStringConcatenation && p.Symbol == "PricingService.Load");
        Assert.Contains(result.Patterns, p => p.PatternId == SecurityPatternIds.WeakHash);
    }

    [Fact]
    public void Complexity_counts_vb_decision_points()
    {
        var tree = Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxTree.ParseText("""
            Class C
                Sub M(a As Integer)
                    If a > 1 OrElse a < -1 Then
                    End If
                    Do While a > 0
                        a -= 1
                    Loop
                    Try
                    Catch ex As Exception
                    End Try
                End Sub
            End Class
            """);
        var method = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax>().Single();

        Assert.Equal(5, VisualBasicComplexityWalker.Measure(method)); // 1 + If + OrElse + Do + Catch
    }
}
