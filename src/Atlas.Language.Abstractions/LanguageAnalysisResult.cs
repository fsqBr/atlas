namespace Atlas.Language.Abstractions;

/// <summary>
/// Normalized, language-neutral facts produced by a language analyzer.
/// Deterministic by construction: everything here is derived from
/// files read through the artifact reader — never inferred, never executed.
/// </summary>
public sealed record LanguageAnalysisResult(
    string LanguageId,
    AnalysisTier TierAchieved,
    IReadOnlyList<SolutionFact> Solutions,
    IReadOnlyList<ProjectFact> Projects,
    IReadOnlyList<FileFact> Files,
    LanguageTotals Totals,
    SymbolResolutionStats? Symbols,
    IReadOnlyList<PatternFact> Patterns,
    IReadOnlyList<MethodFact> HotMethods,
    IReadOnlyList<TypeFact> Types,
    IReadOnlyList<NamespaceDependency> NamespaceDependencies);

public sealed record SolutionFact(string RelativePath, IReadOnlyList<string> ProjectPaths);

public sealed record ProjectFact(
    string RelativePath,
    string Name,
    bool IsSdkStyle,
    string? TargetFramework,
    IReadOnlyList<PackageReferenceFact> PackageReferences,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> AssemblyReferences,
    string? UiFramework = null);

/// <summary>Origin distinguishes SDK-style PackageReference from legacy packages.config entries.</summary>
public sealed record PackageReferenceFact(string Id, string? Version, PackageReferenceOrigin Origin);

public enum PackageReferenceOrigin
{
    PackageReference,
    PackagesConfig,
}

public sealed record FileFact(
    string RelativePath,
    int Lines,
    int TypeCount,
    int MethodCount,
    int MaxCyclomaticComplexity,
    bool HasSyntaxErrors,
    int TestMethodCount);

/// <summary>A method worth naming: reported when complexity crosses the adapter's hot threshold.</summary>
public sealed record MethodFact(string FilePath, string Symbol, int Line, int CyclomaticComplexity, int Lines);

public sealed record TypeFact(string FilePath, string Namespace, string Name, string Kind);

/// <summary>Namespace A references types in namespace B, Weight times (same-assembly, source-resolved).</summary>
public sealed record NamespaceDependency(string From, string To, int Weight);

public sealed record LanguageTotals(
    int FileCount,
    long TotalLines,
    int TypeCount,
    int MethodCount,
    int MaxCyclomaticComplexity,
    double AverageCyclomaticComplexity);

public sealed record SymbolResolutionStats(int SampledInvocations, int ResolvedInvocations)
{
    public double ResolutionRate =>
        SampledInvocations == 0 ? 0 : (double)ResolvedInvocations / SampledInvocations;
}
