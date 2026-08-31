using Atlas.Language.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Language.CSharp;

/// <summary>
/// Dead-code candidates from the Tier 1.75 compilations: top-level internal types that no other
/// source in the analyzed estate references. Conservative by design — reflection, DI-by-convention
/// and serialization cannot be seen statically, so results are emitted as candidates only,
/// framework-instantiated shapes (controllers, attributes, migrations, workers…) are skipped, and
/// any attribute on the declaration disqualifies it. Feed one shared <see cref="SemanticModel"/>
/// per tree with <see cref="Add"/> (binding results are cached per model, so this pass does not
/// repeat the namespace graph's resolution work), then call <see cref="Build"/> once: references
/// are matched across compilations by fully-qualified metadata name, so a type used from another
/// project (a metadata symbol there, not a source symbol) still counts as alive — and a name that
/// fails to bind at all keeps any candidate with that name alive too, because an unresolved usage
/// is evidence of life, never of death.
/// </summary>
public sealed class DeadCodeCollector
{
    private const int MaxCandidatesPerAssembly = 100;

    /// <summary>
    /// Shapes that frameworks discover by convention, reflection or markup the compiler never
    /// sees (routing, assembly scanning, hosted services, EF migrations, XAML/config wiring…).
    /// A zero-reference count means nothing for these.
    /// </summary>
    private static readonly string[] ExcludedSuffixes =
    [
        "Controller", "Attribute", "Hub", "Middleware", "Migration", "Startup", "Program",
        "Page", "Component", "Handler", "Consumer", "Job", "Worker", "Validator", "Profile",
        "Filter", "Convention", "HealthCheck", "Module", "Extensions", "Converter", "Behavior",
        "TagHelper", "Selector", "Section", "Provider",
    ];

    private sealed record Candidate(string Key, string Assembly, string FilePath, int Line, string Kind, string Name, string FullName);

    private readonly List<Candidate> _candidates = [];
    private readonly HashSet<(string Key, string Assembly)> _candidateKeys = [];
    private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolvedNames = new(StringComparer.Ordinal);

    public void Add(SemanticModel model, CancellationToken cancellationToken)
    {
        var root = model.SyntaxTree.GetRoot(cancellationToken);

        // Generated code (designer files, source-generator output committed to the repo) is never
        // a candidate — but its references DO keep hand-written types alive: deleting a type the
        // designer file instantiates breaks the build.
        if (!IsGeneratedPath(model.SyntaxTree.FilePath))
        {
            CollectCandidates(model, root);
        }

        CollectReferences(model, root, cancellationToken);
    }

    /// <summary>Unreferenced candidates as PatternFacts; call once, after every tree was added.</summary>
    public List<PatternFact> Build() =>
        _candidates
            .Where(c => !_referenced.Contains(c.Key) && !_unresolvedNames.Contains(c.Name))
            .OrderBy(c => c.FilePath, StringComparer.Ordinal).ThenBy(c => c.Line)
            .GroupBy(c => c.Assembly)
            .SelectMany(g => g.Take(MaxCandidatesPerAssembly))
            .OrderBy(c => c.FilePath, StringComparer.Ordinal).ThenBy(c => c.Line)
            .Select(c => new PatternFact(
                QualityPatternIds.DeadType, c.FilePath, c.Line, c.Name,
                $"internal {c.Kind} '{c.FullName}' has no source references in the analyzed code"))
            .ToList();

    private void CollectCandidates(SemanticModel model, SyntaxNode root)
    {
        foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            // Top-level types only: a nested type is an implementation detail of its owner.
            if (declaration.Parent is BaseTypeDeclarationSyntax
                || declaration.AttributeLists.Count > 0
                || model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol
                || symbol.DeclaredAccessibility != Accessibility.Internal
                || ExcludedSuffixes.Any(s => symbol.Name.EndsWith(s, StringComparison.Ordinal))
                || HasEntryPoint(declaration))
            {
                continue;
            }

            var key = KeyOf(symbol);
            var assembly = model.Compilation.AssemblyName ?? "<assembly>";
            if (!_candidateKeys.Add((key, assembly)))
            {
                continue; // partial type: first declaration wins
            }

            var line = declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _candidates.Add(new Candidate(
                key, assembly, root.SyntaxTree.FilePath, line, KindOf(declaration), symbol.Name,
                symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{symbol.Name}" : symbol.Name));
        }
    }

    private void CollectReferences(SemanticModel model, SyntaxNode root, CancellationToken cancellationToken)
    {
        var enclosingSymbols = new Dictionary<BaseTypeDeclarationSyntax, INamedTypeSymbol?>();

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = model.GetSymbolInfo(name, cancellationToken);
            // CandidateSymbols keeps cross-project references alive even when accessibility stops
            // full resolution; a name that binds to nothing at all (legacy assembly references,
            // linked files, extension calls on unresolved receivers) is recorded by text so any
            // same-named candidate stays alive.
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is null)
            {
                _unresolvedNames.Add(name.Identifier.ValueText);
                continue;
            }

            var target = (symbol as INamedTypeSymbol ?? symbol.ContainingType)?.OriginalDefinition;
            if (target is null)
            {
                continue;
            }

            // A type mentioning only itself (nested types included) is not alive.
            var enclosing = name.Ancestors().OfType<BaseTypeDeclarationSyntax>().LastOrDefault();
            if (enclosing is not null)
            {
                if (!enclosingSymbols.TryGetValue(enclosing, out var declared))
                {
                    declared = model.GetDeclaredSymbol(enclosing) as INamedTypeSymbol;
                    enclosingSymbols[enclosing] = declared;
                }

                if (declared is not null && SymbolEqualityComparer.Default.Equals(target, declared))
                {
                    continue;
                }
            }

            _referenced.Add(KeyOf(target));
        }
    }

    private static string KeyOf(INamedTypeSymbol symbol) =>
        symbol.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? $"{ns.ToDisplayString()}.{symbol.MetadataName}"
            : symbol.MetadataName;

    private static string KindOf(BaseTypeDeclarationSyntax declaration) => declaration switch
    {
        RecordDeclarationSyntax => "record",
        InterfaceDeclarationSyntax => "interface",
        StructDeclarationSyntax => "struct",
        EnumDeclarationSyntax => "enum",
        _ => "class",
    };

    private static bool HasEntryPoint(BaseTypeDeclarationSyntax declaration) =>
        declaration is TypeDeclarationSyntax type
        && type.Members.OfType<MethodDeclarationSyntax>().Any(m =>
            m.Identifier.Text == "Main" && m.Modifiers.Any(SyntaxKind.StaticKeyword));

    private static bool IsGeneratedPath(string filePath) =>
        filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
}
