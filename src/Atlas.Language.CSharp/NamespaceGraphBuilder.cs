using Atlas.Language.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Language.CSharp;

/// <summary>
/// Namespace-level dependency graph from the Tier 1.75 compilation: every
/// identifier that resolves to a type declared in the analyzed source adds an
/// edge from the referencing type's namespace to the referenced type's namespace.
/// Cross-namespace only; framework/package types are ignored (they are not
/// architecture the customer owns).
/// </summary>
internal static class NamespaceGraphBuilder
{
    public static List<NamespaceDependency> Build(
        CSharpCompilation compilation,
        IReadOnlyList<SyntaxTree> trees,
        CancellationToken cancellationToken)
    {
        var weights = new Dictionary<(string From, string To), int>();

        foreach (var tree in trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(cancellationToken);

            foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var fromNamespace = CSharpLanguageAnalyzer.NamespaceOf(type);

                foreach (var name in type.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
                    var target = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                    if (target is null || !SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, compilation.Assembly))
                    {
                        continue;
                    }

                    var toNamespace = target.ContainingNamespace is { IsGlobalNamespace: false } ns
                        ? ns.ToDisplayString()
                        : "<global>";

                    if (toNamespace == fromNamespace)
                    {
                        continue;
                    }

                    var key = (fromNamespace, toNamespace);
                    weights[key] = weights.GetValueOrDefault(key) + 1;
                }
            }
        }

        return weights
            .Select(kv => new NamespaceDependency(kv.Key.From, kv.Key.To, kv.Value))
            .OrderBy(d => d.From, StringComparer.Ordinal)
            .ThenBy(d => d.To, StringComparer.Ordinal)
            .ToList();
    }
}
