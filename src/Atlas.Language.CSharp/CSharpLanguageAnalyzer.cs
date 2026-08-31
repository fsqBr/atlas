using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Basic.Reference.Assemblies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Language.CSharp;

/// <summary>
/// C# language adapter (006). Tier 1: syntax facts per file, security
/// patterns, hot methods, types. Tier 1.5: solution/project/dependency facts read
/// as XML/text data. Tier "1.75" (SyntacticWithSymbols): a CSharpCompilation
/// assembled from the parsed trees plus bundled netstandard2.0 reference
/// assemblies — symbol resolution and namespace dependencies without MSBuild,
/// without restore, on any OS. No workspace code is ever executed.
/// </summary>
public sealed class CSharpLanguageAnalyzer(RestoredReferences? tier2 = null) : ILanguageAnalyzer
{
    private const int SymbolSampleSize = 1000;

    /// <summary>Methods at or above this cyclomatic complexity are reported individually.</summary>
    public const int HotMethodThreshold = 10;

    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod",
    };

    public LanguageDescriptor Descriptor { get; } = new(
        LanguageId: "csharp",
        Name: "C#",
        Version: "0.3.0",
        Capabilities: ["SyntaxTree", "Symbols", "Dependencies", "SolutionDiscovery", "Metrics", "SecurityPatterns", "PrivacyPatterns", "LegacyApis", "TestDetection", "TypeGraph"]);

    public bool CanAnalyze(IArtifactReader workspace) =>
        workspace.SourceFiles("*.cs").Any();

    public async Task<LanguageAnalysisResult> AnalyzeAsync(
        IArtifactReader workspace,
        CancellationToken cancellationToken)
    {
        // Tier 1.5 — project system as data.
        var solutions = await SolutionFileParser.ParseAllAsync(workspace, cancellationToken);
        var projects = await ProjectFileParser.ParseAllAsync(workspace, cancellationToken);

        // Tier 1 — syntax facts.
        var files = new List<FileFact>();
        var trees = new List<SyntaxTree>();
        var patterns = new List<PatternFact>();
        var hotMethods = new List<MethodFact>();
        var types = new List<TypeFact>();
        var allComplexities = new List<int>();

        foreach (var relativePath in workspace.SourceFiles("*.cs"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = await workspace.ReadAllTextAsync(relativePath, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(text, path: relativePath, cancellationToken: cancellationToken);
            var root = await tree.GetRootAsync(cancellationToken);

            var methods = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().ToList();
            var fileMax = 0;
            foreach (var method in methods)
            {
                var complexity = CyclomaticComplexityWalker.Measure(method);
                allComplexities.Add(complexity);
                fileMax = Math.Max(fileMax, complexity);

                if (complexity >= HotMethodThreshold)
                {
                    var span = method.GetLocation().GetLineSpan();
                    hotMethods.Add(new MethodFact(
                        relativePath,
                        MethodSymbol(method),
                        span.StartLinePosition.Line + 1,
                        complexity,
                        span.EndLinePosition.Line - span.StartLinePosition.Line + 1));
                }
            }

            foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var kind = type switch
                {
                    TypeDeclarationSyntax t => t.Keyword.Text,
                    EnumDeclarationSyntax => "enum",
                    _ => "type",
                };
                types.Add(new TypeFact(relativePath, NamespaceOf(type), type.Identifier.Text, kind));
            }

            files.Add(new FileFact(
                RelativePath: relativePath,
                Lines: tree.GetText(cancellationToken).Lines.Count,
                TypeCount: root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Count(),
                MethodCount: methods.Count,
                MaxCyclomaticComplexity: fileMax,
                HasSyntaxErrors: tree.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error),
                TestMethodCount: root.DescendantNodes().OfType<AttributeSyntax>().Count(IsTestAttribute)));

            patterns.AddRange(SecurityPatternWalker.Collect(tree, relativePath, cancellationToken));
            patterns.AddRange(SensitiveDataWalker.Collect(tree, relativePath, cancellationToken));
            patterns.AddRange(LegacyApiWalker.Collect(tree, relativePath, cancellationToken));
            trees.Add(tree);
        }

        var totals = new LanguageTotals(
            FileCount: files.Count,
            TotalLines: files.Sum(f => (long)f.Lines),
            TypeCount: files.Sum(f => f.TypeCount),
            MethodCount: files.Sum(f => f.MethodCount),
            MaxCyclomaticComplexity: allComplexities.Count == 0 ? 0 : allComplexities.Max(),
            AverageCyclomaticComplexity: allComplexities.Count == 0 ? 0 : allComplexities.Average());

        // Tier 1.75 — symbols + namespace graph from trees + bundled reference assemblies (no build).
        SymbolResolutionStats? symbols = null;
        var namespaceDependencies = new List<NamespaceDependency>();
        var tier = AnalysisTier.Syntactic;

        if (trees.Count > 0)
        {
            // One compilation per project (topological order, referenced projects as metadata references),
            // plus a catch-all for files outside any project. Cross-project symbols resolve; duplicate type
            // names in unrelated projects no longer collide.
            IReadOnlyDictionary<ProjectFact, IReadOnlyList<MetadataReference>>? restored = null;
            if (tier2 is { Enabled: true })
            {
                restored = await tier2.RestoreAsync(workspace.RootPath, projects, solutions, cancellationToken);
            }

            var compilations = ProjectCompilations.Build(projects, trees, restored);
            var deadCode = new DeadCodeCollector();
            var sampled = 0;
            var resolved = 0;
            foreach (var (compilation, projectTrees) in compilations)
            {
                var compilationSampled = 0;
                foreach (var tree in projectTrees)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // One semantic model per tree, shared by every consumer below: binding results
                    // are cached per model, so the sampler, [Obsolete] detection, the namespace
                    // graph and dead-code collection do not repeat each other's resolution work.
                    var model = compilation.GetSemanticModel(tree);
                    var stats = ResolveSymbolsSample(model, SymbolSampleSize - compilationSampled, cancellationToken);
                    compilationSampled += stats.SampledInvocations;
                    sampled += stats.SampledInvocations;
                    resolved += stats.ResolvedInvocations;
                    patterns.AddRange(ObsoleteApiDetector.Collect(model, tree.FilePath, cancellationToken));
                    namespaceDependencies.AddRange(NamespaceGraphBuilder.Collect(model, cancellationToken));
                    deadCode.Add(model, cancellationToken);
                }
            }

            var deadTypes = deadCode.Build();
            if (deadTypes.Count > 0)
            {
                // XAML, Razor/WebForms markup and config files instantiate types the compiler never
                // sees a reference to; a name occurring in any of them keeps the type alive.
                var markupTexts = await ReadMarkupAndConfigTextsAsync(workspace, cancellationToken);
                deadTypes = deadTypes.Where(f =>
                {
                    var simpleName = f.Symbol[(f.Symbol.LastIndexOf('.') + 1)..];
                    return !markupTexts.Any(t => t.Contains(simpleName, StringComparison.Ordinal));
                }).ToList();
            }

            patterns.AddRange(deadTypes);
            symbols = new SymbolResolutionStats(sampled, resolved);
            namespaceDependencies = namespaceDependencies
                .GroupBy(d => (d.From, d.To))
                .Select(g => new NamespaceDependency(g.Key.From, g.Key.To, g.Sum(d => d.Weight)))
                .ToList();
            tier = restored is { Count: > 0 } ? AnalysisTier.DesignTime : AnalysisTier.SyntacticWithSymbols;
        }

        return new LanguageAnalysisResult(
            Descriptor.LanguageId,
            tier,
            solutions,
            projects,
            files,
            totals,
            symbols,
            patterns,
            hotMethods,
            types,
            namespaceDependencies);
    }

    private static readonly string[] MarkupAndConfigPatterns = ["*.xaml", "*.cshtml", "*.razor", "*.aspx", "*.ascx", "*.master", "*.config"];

    private static async Task<List<string>> ReadMarkupAndConfigTextsAsync(IArtifactReader workspace, CancellationToken cancellationToken)
    {
        var texts = new List<string>();
        foreach (var pattern in MarkupAndConfigPatterns)
        {
            foreach (var relativePath in workspace.SourceFiles(pattern))
            {
                cancellationToken.ThrowIfCancellationRequested();
                texts.Add(await workspace.ReadAllTextAsync(relativePath, cancellationToken));
            }
        }

        return texts;
    }

    private static bool IsTestAttribute(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            _ => attribute.Name.ToString(),
        };

        return TestAttributes.Contains(name) || TestAttributes.Contains(name.Replace("Attribute", string.Empty));
    }

    internal static string MethodSymbol(BaseMethodDeclarationSyntax method)
    {
        var type = method.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        var name = method switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            DestructorDeclarationSyntax => "~dtor",
            OperatorDeclarationSyntax o => "operator" + o.OperatorToken.Text,
            ConversionOperatorDeclarationSyntax => "conversion",
            _ => "<method>",
        };

        return type is null ? name : $"{type.Identifier.Text}.{name}";
    }

    internal static string NamespaceOf(SyntaxNode node)
    {
        var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        return ns?.Name.ToString() ?? "<global>";
    }

    private static SymbolResolutionStats ResolveSymbolsSample(SemanticModel model, int budget, CancellationToken cancellationToken)
    {
        int sampled = 0, resolved = 0;
        if (budget <= 0)
        {
            return new SymbolResolutionStats(0, 0);
        }

        foreach (var invocation in model.SyntaxTree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (sampled >= budget)
            {
                break;
            }

            sampled++;
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not null)
            {
                resolved++;
            }
        }

        return new SymbolResolutionStats(sampled, resolved);
    }
}
