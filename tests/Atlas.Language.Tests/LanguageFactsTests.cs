using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Scanner.Runtime;

namespace Atlas.Language.Tests;

/// <summary>Test detection, hot methods, types and namespace graph from the Tier 1.75 compilation.</summary>
public class LanguageFactsTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-langfacts").FullName;
    private LanguageAnalysisResult _result = null!;

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_root, "Domain.cs"), """
            namespace Shop.Domain
            {
                public class Order
                {
                    public int Total(int[] items, bool vip, bool rush)
                    {
                        var total = 0;
                        foreach (var i in items) { if (i > 0) total += i; }
                        if (vip) total -= 1;
                        if (rush) total += 2;
                        if (total > 100 && vip) total -= 5;
                        if (total > 200 || rush) total += 1;
                        if (items.Length == 0) return 0;
                        if (items.Length > 50) total++;
                        return total > 0 ? total : 0;
                    }
                }
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Api.cs"), """
            namespace Shop.Api
            {
                public class OrdersController
                {
                    public int Get() => new Shop.Domain.Order().Total(new[] { 1 }, false, false);
                }
            }
            """);

        File.WriteAllText(Path.Combine(_root, "OrderTests.cs"), """
            namespace Shop.Tests
            {
                public class OrderTests
                {
                    [Fact] public void A() { }
                    [Theory] public void B(int x) { }
                    [Xunit.FactAttribute] public void C() { }
                    public void Helper() { }
                }
            }
            """);

        // Build output and package caches must never count as the customer's source.
        Directory.CreateDirectory(Path.Combine(_root, "obj", "Debug"));
        File.WriteAllText(Path.Combine(_root, "obj", "Debug", "Shop.AssemblyInfo.cs"), "[assembly: System.Reflection.AssemblyTitleAttribute(\"Shop\")]");
        Directory.CreateDirectory(Path.Combine(_root, "packages", "Some.Lib.1.0", "content"));
        File.WriteAllText(Path.Combine(_root, "packages", "Some.Lib.1.0", "content", "Vendored.cs"), "namespace Vendor { public class V { } }");

        _result = await new CSharpLanguageAnalyzer().AnalyzeAsync(new ContainedArtifactReader(_root), CancellationToken.None);
    }

    [Fact]
    public void Ignores_build_output_and_package_caches()
    {
        Assert.Equal(3, _result.Files.Count);
        Assert.DoesNotContain(_result.Files, f => f.RelativePath.Contains("obj"));
        Assert.DoesNotContain(_result.Types, t => t.Namespace == "Vendor");
    }

    [Fact]
    public void Counts_test_methods_per_file()
    {
        Assert.Equal(3, _result.Files.Single(f => f.RelativePath.EndsWith("OrderTests.cs")).TestMethodCount);
        Assert.Equal(0, _result.Files.Single(f => f.RelativePath.EndsWith("Domain.cs")).TestMethodCount);
    }

    [Fact]
    public void Reports_hot_methods_with_symbol_and_complexity()
    {
        var hot = Assert.Single(_result.HotMethods);
        Assert.Equal("Order.Total", hot.Symbol);
        Assert.True(hot.CyclomaticComplexity >= CSharpLanguageAnalyzer.HotMethodThreshold, $"complexity was {hot.CyclomaticComplexity}");
        Assert.True(hot.Lines > 5);
    }

    [Fact]
    public void Lists_types_with_namespaces()
    {
        Assert.Contains(_result.Types, t => t.Name == "Order" && t.Namespace == "Shop.Domain" && t.Kind == "class");
        Assert.Contains(_result.Types, t => t.Name == "OrdersController" && t.Namespace == "Shop.Api");
        Assert.Equal(3, _result.Types.Count);
    }

    [Fact]
    public void Builds_cross_namespace_dependencies_from_source_symbols()
    {
        var edge = Assert.Single(_result.NamespaceDependencies);
        Assert.Equal("Shop.Api", edge.From);
        Assert.Equal("Shop.Domain", edge.To);
        Assert.True(edge.Weight >= 1);
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

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }
}
