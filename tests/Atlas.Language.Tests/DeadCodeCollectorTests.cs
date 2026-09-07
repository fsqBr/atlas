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

    [Fact]
    public void Flags_unreferenced_private_members_but_not_used_or_magic_ones()
    {
        var facts = Collect(("A.cs", """
            namespace App;
            public class Service
            {
                private int _used;
                private int _dead;
                private void Helper() { _used++; }
                private void Orphan() { }
                private void OnEnable() { }
                public void Run() => Helper();
            }
            """));

        Assert.Contains(facts, f => f.PatternId == QualityPatternIds.DeadMember && f.Symbol == "Service.Orphan");
        Assert.Contains(facts, f => f.Symbol == "Service._dead");
        Assert.DoesNotContain(facts, f => f.Symbol == "Service._used");
        Assert.DoesNotContain(facts, f => f.Symbol == "Service.Helper");
        Assert.DoesNotContain(facts, f => f.Symbol == "Service.OnEnable"); // engine magic name
    }

    [Fact]
    public void Recursive_self_call_does_not_keep_a_member_alive_and_dead_types_suppress_their_members()
    {
        var facts = Collect(("A.cs", """
            namespace App;
            public class Host
            {
                private int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
            }
            internal class Orphanage
            {
                private void Lonely() { }
            }
            """));

        Assert.Contains(facts, f => f.PatternId == QualityPatternIds.DeadMember && f.Symbol == "Host.Fib");
        Assert.Contains(facts, f => f.PatternId == QualityPatternIds.DeadType && f.Symbol == "Orphanage");
        Assert.DoesNotContain(facts, f => f.Symbol == "Orphanage.Lonely"); // the dead type already covers it
    }

    [Fact]
    public void Names_mentioned_in_string_literals_stay_alive()
    {
        var facts = Collect(("A.cs", """
            namespace App;
            public class Reflector
            {
                private void Recalc() { }
                public object? M() => GetType().GetMethod("Recalc");
            }
            internal class NamedInConfigKey { }
            internal class Ghost { }
            public class K { public string Key = "cache:NamedInConfigKey:v1"; }
            """));

        Assert.DoesNotContain(facts, f => f.Symbol == "Reflector.Recalc");
        Assert.DoesNotContain(facts, f => f.Symbol == "NamedInConfigKey");
        Assert.Contains(facts, f => f.Symbol == "Ghost");
    }

    [Fact]
    public void Serializable_type_fields_attributed_members_and_explicit_implementations_are_not_candidates()
    {
        var facts = Collect(("A.cs", """
            namespace App;
            [System.Serializable]
            public class Snapshot
            {
                private int _state;
                private void Rebuild() { }
            }
            public interface IPing { void Ping(); }
            public class Handlers : IPing
            {
                void IPing.Ping() { }
                [System.Obsolete] private void Marked() { }
                private event System.EventHandler? Poked;
                private string Unseen { get; set; } = "";
            }
            """));

        Assert.DoesNotContain(facts, f => f.Symbol == "Snapshot._state"); // [Serializable] serializes private fields
        Assert.Contains(facts, f => f.Symbol == "Snapshot.Rebuild"); // methods still are candidates
        Assert.DoesNotContain(facts, f => f.Symbol.Contains("Ping")); // explicit interface implementation
        Assert.DoesNotContain(facts, f => f.Symbol == "Handlers.Marked"); // attributed member
        Assert.Contains(facts, f => f.Symbol == "Handlers.Poked" && f.Detail.Contains("private event"));
        Assert.Contains(facts, f => f.Symbol == "Handlers.Unseen" && f.Detail.Contains("private property"));
    }

    [Fact]
    public void Nested_type_members_inside_a_dead_type_are_suppressed_and_nested_keys_do_not_collide()
    {
        var facts = Collect(
            ("A.cs", "namespace App; internal class Orphan { private sealed class Inner { private void Solo() { } } }"),
            ("B.cs", "namespace App; public class Inner { private void Solo() { } }"));

        Assert.Contains(facts, f => f.PatternId == QualityPatternIds.DeadType && f.Symbol == "Orphan");
        // Exactly one Inner.Solo: the top-level Inner's member. The nested Orphan+Inner one is
        // covered by the dead type, and its key must not swallow the top-level candidate.
        Assert.Single(facts, f => f.Symbol == "Inner.Solo");
        Assert.Single(facts, f => f.PatternId == QualityPatternIds.DeadMember);
    }

    [Fact]
    public void Same_named_member_in_a_different_type_is_a_real_reference_not_recursion()
    {
        var facts = Collect(
            ("A.cs", "namespace One; public class T { private static void X() { } }"),
            ("B.cs", "namespace Two; public class T { private void X() { One.T.X(); } }"));

        Assert.DoesNotContain(facts, f => f.FilePath == "A.cs" && f.Symbol == "T.X"); // used from Two.T.X — not recursion
        Assert.Contains(facts, f => f.FilePath == "B.cs" && f.Symbol == "T.X"); // Two.T.X itself is unreferenced
    }

    [Fact]
    public void Raw_string_literals_also_keep_names_alive()
    {
        var facts = Collect(("A.cs", "namespace App;\npublic class R { private void Hook() { } public string J() => \"\"\"{ \"handler\": \"Hook\" }\"\"\"; }"));

        Assert.DoesNotContain(facts, f => f.Symbol == "R.Hook");
    }
}
