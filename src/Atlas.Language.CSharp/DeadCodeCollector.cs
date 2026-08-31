using Atlas.Language.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Language.CSharp;

/// <summary>
/// Dead-code candidates from the Tier 1.75 compilations: top-level internal types — and, since
/// v0.45, private members (methods, fields, properties, events) — that nothing else in the
/// analyzed estate references. Conservative by design: reflection, DI-by-convention and
/// serialization cannot be seen statically, so results are candidates only; framework-instantiated
/// shapes are skipped, any attribute disqualifies, and three "evidence of life" channels keep
/// items alive — names the compiler fails to bind, identifier-shaped words inside string literals
/// (reflection by name), and, at the analyzer level, names occurring in markup/config files.
/// Feed one shared <see cref="SemanticModel"/> per tree with <see cref="Add"/> (binding results
/// are cached per model, so this pass does not repeat the namespace graph's resolution work),
/// then call <see cref="Build"/> once: references are matched across compilations by
/// fully-qualified metadata name, so a type or member used from another project still counts as
/// alive.
/// </summary>
public sealed class DeadCodeCollector
{
    private const int MaxTypesPerAssembly = 100;
    private const int MaxMembersPerAssembly = 150;
    private const int MaxMentionLength = 128;

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

    /// <summary>Private methods engines call by exact name without a source reference (Unity, WinForms designer).</summary>
    private static readonly HashSet<string> MagicMethodNames = new(StringComparer.Ordinal)
    {
        "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable",
        "OnDestroy", "OnGUI", "OnValidate", "Reset", "InitializeComponent",
    };

    private sealed record Candidate(string Key, string Assembly, string FilePath, int Line, string Kind, string Name, string FullName);

    private readonly List<Candidate> _types = [];
    private readonly List<Candidate> _members = [];
    private readonly HashSet<(string Key, string Assembly)> _candidateKeys = [];
    private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolvedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mentionedInStrings = new(StringComparer.Ordinal);

    public void Add(SemanticModel model, CancellationToken cancellationToken)
    {
        var root = model.SyntaxTree.GetRoot(cancellationToken);

        // Generated code (designer files, source-generator output committed to the repo) is never
        // a candidate — but its references DO keep hand-written code alive: deleting a type the
        // designer file instantiates breaks the build.
        if (!IsGeneratedPath(model.SyntaxTree.FilePath))
        {
            CollectTypeCandidates(model, root);
            CollectMemberCandidates(model, root);
        }

        CollectReferences(model, root, cancellationToken);
        CollectStringMentions(root);
    }

    /// <summary>Unreferenced candidates as PatternFacts; call once, after every tree was added.</summary>
    public List<PatternFact> Build()
    {
        bool Alive(Candidate c) =>
            _referenced.Contains(c.Key) || _unresolvedNames.Contains(c.Name) || _mentionedInStrings.Contains(c.Name);

        IEnumerable<Candidate> Capped(IEnumerable<Candidate> dead, int cap) => dead
            .OrderBy(c => c.FilePath, StringComparer.Ordinal).ThenBy(c => c.Line)
            .GroupBy(c => c.Assembly)
            .SelectMany(g => g.Take(cap))
            .OrderBy(c => c.FilePath, StringComparer.Ordinal).ThenBy(c => c.Line);

        var deadTypes = _types.Where(c => !Alive(c)).ToList();
        var deadTypeKeys = deadTypes.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        var facts = Capped(deadTypes, MaxTypesPerAssembly)
            .Select(c => new PatternFact(
                QualityPatternIds.DeadType, c.FilePath, c.Line, c.Name,
                $"internal {c.Kind} '{c.FullName}' has no source references in the analyzed code"))
            .ToList();

        // A dead TYPE already covers all of its members: one finding, not thirty. The owner chain
        // walk also covers members of NESTED types inside a dead top-level type.
        var deadMembers = _members.Where(c => !Alive(c) && !OwnerChain(c.Key).Any(deadTypeKeys.Contains));
        facts.AddRange(Capped(deadMembers, MaxMembersPerAssembly)
            .Select(c => new PatternFact(
                QualityPatternIds.DeadMember, c.FilePath, c.Line, c.FullName,
                $"private {c.Kind} '{c.FullName}' has no source references in the analyzed code")));
        return facts;
    }

    /// <summary>The member's containing type key, then each enclosing type key ("Ns.Outer+Inner" → "Ns.Outer").</summary>
    private static IEnumerable<string> OwnerChain(string memberKey)
    {
        var owner = memberKey[..memberKey.LastIndexOf("::", StringComparison.Ordinal)];
        yield return owner;
        for (var plus = owner.LastIndexOf('+'); plus > 0; plus = owner.LastIndexOf('+'))
        {
            owner = owner[..plus];
            yield return owner;
        }
    }

    private void CollectTypeCandidates(SemanticModel model, SyntaxNode root)
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
            _types.Add(new Candidate(
                key, assembly, root.SyntaxTree.FilePath, line, KindOf(declaration), symbol.Name,
                symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{symbol.Name}" : symbol.Name));
        }
    }

    private void CollectMemberCandidates(SemanticModel model, SyntaxNode root)
    {
        void TryAdd(ISymbol? symbol, SyntaxToken identifier, string kind, bool containerAttributed)
        {
            if (symbol is null
                || symbol.DeclaredAccessibility != Accessibility.Private
                || MagicMethodNames.Contains(symbol.Name)
                || IsExplicitInterfaceImplementation(symbol)
                // [Serializable]/type-level attributes can imply reflection over FIELDS.
                || (containerAttributed && symbol is IFieldSymbol))
            {
                return;
            }

            var containing = symbol.ContainingType;
            if (containing is null)
            {
                return;
            }

            var key = MemberKeyOf(containing, symbol.Name);
            var assembly = model.Compilation.AssemblyName ?? "<assembly>";
            if (!_candidateKeys.Add((key, assembly)))
            {
                return; // overloads share the key: first wins, any overload's use keeps all alive
            }

            var line = identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _members.Add(new Candidate(
                key, assembly, root.SyntaxTree.FilePath, line, kind, symbol.Name, $"{containing.Name}.{symbol.Name}"));
        }

        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (member.AttributeLists.Count > 0 || member.Modifiers.Any(SyntaxKind.PartialKeyword) || member.Modifiers.Any(SyntaxKind.ExternKeyword))
            {
                continue;
            }

            var containerAttributed = member.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                .Any(t => t.AttributeLists.Count > 0);

            switch (member)
            {
                case MethodDeclarationSyntax method when method.Identifier.Text != "Main":
                    TryAdd(model.GetDeclaredSymbol(method), method.Identifier, "method", containerAttributed);
                    break;
                case PropertyDeclarationSyntax property:
                    TryAdd(model.GetDeclaredSymbol(property), property.Identifier, "property", containerAttributed);
                    break;
                case EventDeclarationSyntax @event:
                    TryAdd(model.GetDeclaredSymbol(@event), @event.Identifier, "event", containerAttributed);
                    break;
                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        TryAdd(model.GetDeclaredSymbol(variable), variable.Identifier, "event", containerAttributed);
                    }

                    break;
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        TryAdd(model.GetDeclaredSymbol(variable), variable.Identifier, "field", containerAttributed);
                    }

                    break;
            }
        }
    }

    private void CollectReferences(SemanticModel model, SyntaxNode root, CancellationToken cancellationToken)
    {
        var enclosingTypes = new Dictionary<BaseTypeDeclarationSyntax, INamedTypeSymbol?>();

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

            if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
            {
                symbol = reduced;
            }

            // Member reference (skip recursion: a member mentioning only itself is not alive).
            if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
                && symbol.ContainingType is { } memberOwner
                && !IsRecursiveSelfReference(model, enclosingTypes, name, symbol))
            {
                _referenced.Add(MemberKeyOf(memberOwner.OriginalDefinition, symbol.Name));
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
                if (!enclosingTypes.TryGetValue(enclosing, out var declared))
                {
                    declared = model.GetDeclaredSymbol(enclosing) as INamedTypeSymbol;
                    enclosingTypes[enclosing] = declared;
                }

                if (declared is not null && SymbolEqualityComparer.Default.Equals(target, declared))
                {
                    continue;
                }
            }

            _referenced.Add(KeyOf(target));
        }
    }

    /// <summary>
    /// Identifier-shaped words inside string literals: reflection, config keys and binding paths
    /// address code by name ("nameof" binds, but "GetMethod(\"Recalc\")" does not).
    /// </summary>
    private void CollectStringMentions(SyntaxNode root)
    {
        foreach (var token in root.DescendantTokens())
        {
            if (token.Kind() is not (SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken
                or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.Utf8SingleLineRawStringLiteralToken
                or SyntaxKind.Utf8MultiLineRawStringLiteralToken))
            {
                continue;
            }

            // Long literals (raw-string templates, embedded JSON/config) are scanned up to the cap
            // instead of skipped — that is exactly where reflection-by-name mentions live.
            var text = token.ValueText;
            if (text.Length == 0)
            {
                continue;
            }

            if (text.Length > MaxMentionLength)
            {
                text = text[..MaxMentionLength];
            }

            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var isIdent = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
                if (isIdent && start < 0)
                {
                    start = i;
                }
                else if (!isIdent && start >= 0)
                {
                    if (i - start >= 2 && !char.IsDigit(text[start]))
                    {
                        _mentionedInStrings.Add(text[start..i]);
                    }

                    start = -1;
                }
            }
        }
    }

    private static bool IsRecursiveSelfReference(
        SemanticModel model,
        Dictionary<BaseTypeDeclarationSyntax, INamedTypeSymbol?> typeCache,
        SimpleNameSyntax name,
        ISymbol member)
    {
        foreach (var ancestor in name.Ancestors())
        {
            var identifier = ancestor switch
            {
                MethodDeclarationSyntax m => m.Identifier.Text,
                PropertyDeclarationSyntax p => p.Identifier.Text,
                VariableDeclaratorSyntax v when v.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax => v.Identifier.Text,
                EventDeclarationSyntax e => e.Identifier.Text,
                _ => null,
            };

            if (identifier is null)
            {
                continue;
            }

            if (identifier != member.Name)
            {
                return false;
            }

            // Same simple name: confirm SEMANTICALLY that the enclosing type is the member's own
            // type — a call from B.T.X() to A.T.X() (same names, different types) is a real use.
            var enclosingType = ancestor.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            if (enclosingType is null)
            {
                return false;
            }

            if (!typeCache.TryGetValue(enclosingType, out var declaredType))
            {
                declaredType = model.GetDeclaredSymbol(enclosingType) as INamedTypeSymbol;
                typeCache[enclosingType] = declaredType;
            }

            return declaredType is not null
                && SymbolEqualityComparer.Default.Equals(declaredType.OriginalDefinition, member.ContainingType?.OriginalDefinition);
        }

        return false;
    }

    private static bool IsExplicitInterfaceImplementation(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.ExplicitInterfaceImplementations.Length > 0,
        IPropertySymbol p => p.ExplicitInterfaceImplementations.Length > 0,
        IEventSymbol e => e.ExplicitInterfaceImplementations.Length > 0,
        _ => false,
    };

    private static string KeyOf(INamedTypeSymbol symbol)
    {
        // Full containing-type chain ("Ns.Outer+Inner"): a nested type must never share a key
        // with a same-named top-level type, and dead-type suppression walks this chain.
        var path = symbol.MetadataName;
        for (var outer = symbol.ContainingType; outer is not null; outer = outer.ContainingType)
        {
            path = outer.MetadataName + "+" + path;
        }

        return symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? $"{ns.ToDisplayString()}.{path}" : path;
    }

    private static string MemberKeyOf(INamedTypeSymbol containingType, string memberName) =>
        $"{KeyOf(containingType)}::{memberName}";

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
