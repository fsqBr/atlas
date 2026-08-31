using Atlas.Language.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Language.CSharp;

/// <summary>
/// Namespace-level dependency graph from the Tier 1.75 compilation: every
/// identifier that resolves to a type declared in the analyzed source adds an
/// edge from the referencing type's namespace to the referenced type's namespace.
/// Cross-namespace only; framework/package types are ignored (they are not
/// architecture the customer owns). Takes the caller's per-tree
/// <see cref="SemanticModel"/> so the binding cache is shared with the other
/// semantic passes; the analyzer merges the per-tree edges afterwards.
/// </summary>
internal static class NamespaceGraphBuilder
{
    public static List<NamespaceDependency> Collect(SemanticModel model, CancellationToken cancellationToken)
    {
        var weights = new Dictionary<(string From, string To), int>();
        var root = model.SyntaxTree.GetRoot(cancellationToken);

        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fromNamespace = CSharpLanguageAnalyzer.NamespaceOf(type);

            foreach (var name in type.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
                var target = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                if (target is null || !SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, model.Compilation.Assembly))
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

        return weights
            .Select(kv => new NamespaceDependency(kv.Key.From, kv.Key.To, kv.Value))
            .ToList();
    }
}
