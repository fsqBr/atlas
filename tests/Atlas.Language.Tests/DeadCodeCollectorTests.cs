using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Atlas.Language.Tests;

public class DeadCodeCollectorTests
{
    private static List<PatternFact> Collect(params (string Path, string Source)[] files)
    {
        var trees = files.Select(f => (SyntaxTree)CSharpSyntaxTree.ParseText(f.Source, path: f.Path)).ToList();
        var compilation = CSharpCompilation.Create("TestAssembly", trees);
        var collector = new DeadCodeCollector();
        foreach (var tree in trees)
        {
            collector.Add(compilation.GetSemanticModel(tree), CancellationToken.None);
        }

        return collector.Build();
    }

    [Fact]
    public void Flags_unreferenced_internal_type_but_not_referenced_or_public_ones()
    {
        var facts = Collect(
            ("A.cs", "namespace App; internal class Orphan { } internal class Used { } public class Api { }"),
            ("B.cs", "namespace App; internal class Caller { public Used U() => new Used(); }"));

        Assert.Contains(facts, f => f.Symbol == "Orphan" && f.PatternId == QualityPatternIds.DeadType);
        Assert.Contains(facts, f => f.Detail.Contains("internal class 'App.Orphan'"));
        Assert.DoesNotContain(facts, f => f.Symbol == "Used");
        Assert.DoesNotContain(facts, f => f.Symbol == "Api"); // public: may be consumed outside the estate
        Assert.Contains(facts, f => f.Symbol == "Caller"); // nothing references Caller either
    }

    [Fact]
    public void Skips_framework_shapes_attributes_and_entry_points()
    {
        var facts = Collect(("A.cs", """
            namespace App;
            internal class OrdersController { }
            internal class CleanupWorker { }
            internal static class StringExtensions { }
            internal class BoolConverter { }
            [System.Serializable] internal class Wire { }
            internal class EntryHost { static void Main(string[] args) { } }
            internal class Plain { }
            """));

        Assert.Single(facts);
        Assert.Equal("Plain", facts[0].Symbol);
    }

    [Fact]
    public void Self_reference_does_not_keep_a_type_alive()
    {
        var facts = Collect(("A.cs", "namespace App; internal class Loner { public Loner? Next; public Loner Self() => this; }"));

        Assert.Contains(facts, f => f.Symbol == "Loner");
    }

    [Fact]
    public void Base_list_typeof_and_cross_type_references_count_as_alive()
    {
        var facts = Collect(
            ("A.cs", "namespace App; internal interface IThing { } internal enum Mode { On } internal class Widget { }"),
            ("B.cs", "namespace App; internal class Impl : IThing { public Mode M; public object T() => typeof(Widget); }"));

        Assert.DoesNotContain(facts, f => f.Symbol is "IThing" or "Mode" or "Widget");
        Assert.Contains(facts, f => f.Symbol == "Impl");
    }

    [Fact]
    public void Nested_types_and_generated_files_are_not_candidates()
    {
        var facts = Collect(
            ("A.cs", "namespace App; internal class Outer { internal class Inner { } }"),
            ("Gen.g.cs", "namespace App; internal class FromGenerator { }"));

        Assert.Contains(facts, f => f.Symbol == "Outer");
        Assert.DoesNotContain(facts, f => f.Symbol is "Inner" or "FromGenerator");
    }

    [Fact]
    public void References_inside_generated_files_keep_hand_written_types_alive()
    {
        var facts = Collect(
            ("Widgets.cs", "namespace App; internal class GridPanel { } internal class DeadOne { }"),
            ("Form1.Designer.cs", "namespace App; partial class Form1 { GridPanel? gridPanel; }"));

        Assert.DoesNotContain(facts, f => f.Symbol == "GridPanel"); // deleting it would break the designer file
        Assert.DoesNotContain(facts, f => f.Symbol == "Form1"); // declared only in a generated file: not a candidate
        Assert.Contains(facts, f => f.Symbol == "DeadOne");
    }

    [Fact]
    public void Unresolved_name_keeps_same_named_candidates_alive()
    {
        // "Helper.Do()" does not bind (no reference between the compilations), so the usage is
        // only visible by name — evidence of life, never of death.
        var coreTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText("namespace Core; internal class Helper { public static void Do() { } } internal class Dead { }", path: "Core.cs") };
        var core = CSharpCompilation.Create("Core", coreTrees);
        var appTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText("namespace App; class U { void M() { Helper.Do(); } }", path: "App.cs") };
        var app = CSharpCompilation.Create("App", appTrees);

        var collector = new DeadCodeCollector();
        foreach (var tree in coreTrees) collector.Add(core.GetSemanticModel(tree), CancellationToken.None);
        foreach (var tree in appTrees) collector.Add(app.GetSemanticModel(tree), CancellationToken.None);
        var facts = collector.Build();

        Assert.DoesNotContain(facts, f => f.Symbol == "Helper");
        Assert.Contains(facts, f => f.Symbol == "Dead");
    }

    [Fact]
    public void Generic_types_match_references_by_metadata_name()
    {
        var facts = Collect(
            ("A.cs", "namespace App; internal class Cache<T> { } internal class Bag<T> { }"),
            ("B.cs", "namespace App; internal class Uses { public Cache<int>? C; }"));

        Assert.DoesNotContain(facts, f => f.Symbol == "Cache");
        Assert.Contains(facts, f => f.Symbol == "Bag");
    }

    [Fact]
    public void Reference_from_another_compilation_keeps_the_type_alive()
    {
        var coreTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText("namespace Core; internal class Shared { } internal class Unused { }", path: "Core.cs") };
        var core = CSharpCompilation.Create("Core", coreTrees);
        var appTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText("namespace App; internal class User { public Core.Shared? S; }", path: "App.cs") };
        var app = CSharpCompilation.Create("App", appTrees, [core.ToMetadataReference()]);

        var collector = new DeadCodeCollector();
        foreach (var tree in coreTrees) collector.Add(core.GetSemanticModel(tree), CancellationToken.None);
        foreach (var tree in appTrees) collector.Add(app.GetSemanticModel(tree), CancellationToken.None);
        var facts = collector.Build();

        Assert.DoesNotContain(facts, f => f.Symbol == "Shared");
        Assert.Contains(facts, f => f.Symbol == "Unused");
        Assert.Contains(facts, f => f.Symbol == "User");
    }
}
