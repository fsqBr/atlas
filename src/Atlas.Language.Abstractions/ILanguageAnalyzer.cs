using Atlas.Domain.Workspaces;

namespace Atlas.Language.Abstractions;

/// <summary>
/// Language adapter contract. The core only knows this
/// interface; adding a language is a new package plus registration. Analyzers
/// read exclusively through IArtifactReader and never execute workspace code
/// — the tier actually achieved is declared on every result.
/// </summary>
public interface ILanguageAnalyzer
{
    LanguageDescriptor Descriptor { get; }

    bool CanAnalyze(IArtifactReader workspace);

    Task<LanguageAnalysisResult> AnalyzeAsync(IArtifactReader workspace, CancellationToken cancellationToken);
}

public sealed record LanguageDescriptor(
    string LanguageId,
    string Name,
    string Version,
    IReadOnlyCollection<string> Capabilities);

/// <summary>Analysis depth achieved for a given workspace (tiers).</summary>
public enum AnalysisTier
{
    /// <summary>Pure syntax trees; always available on any OS.</summary>
    Syntactic = 1,

    /// <summary>Syntax trees + compilation assembled from them with bundled reference assemblies — symbols without any build.</summary>
    SyntacticWithSymbols = 2,

    /// <summary>Design-time build (e.g. Buildalyzer) succeeded; project-accurate references.</summary>
    DesignTime = 3,

    /// <summary>Full compilation available.</summary>
    Full = 4,
}
