using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;

namespace Atlas.Scanner.Abstractions;

/// <summary>
/// Core scanner contract. Scanners consume a normalized workspace
/// and language facts, emit finding candidates through the sink, and declare the
/// rules they can emit. They never touch persistence, UI or a provider API, and
/// never execute workspace code.
/// </summary>
public interface IScanner
{
    ScannerDescriptor Descriptor { get; }

    /// <summary>Every rule this scanner may emit; upserted into the rule catalog before reconciliation.</summary>
    IReadOnlyList<RuleSpec> Rules { get; }

    Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken);
}

public sealed record ScannerDescriptor(
    string Id,
    string Name,
    string Version,
    FindingCategory Category,
    IReadOnlyCollection<string> Capabilities);

/// <summary>
/// A rule as declared by its scanner. Title/Description/Remediation are the
/// English canonical text; Localizations adds other languages plus optional
/// title/message templates rendered from finding data (see RuleLocalization).
/// </summary>
public sealed record RuleSpec(
    string Id,
    string Version,
    FindingCategory Category,
    Severity DefaultSeverity,
    string Title,
    string Description,
    string? Remediation = null,
    IReadOnlyDictionary<string, Atlas.Domain.Rules.RuleLocalization>? Localizations = null);

public sealed record ScanResult(bool Succeeded, string? Error = null)
{
    public static ScanResult Success() => new(true);

    public static ScanResult Failure(string error) => new(false, error);
}

/// <summary>Everything a scanner is allowed to see: the workspace, language facts, and a sink.</summary>
public sealed class ScanContext
{
    public required Guid AssessmentId { get; init; }
    public required Guid ScanId { get; init; }
    public required string RepositoryKey { get; init; }
    public required IArtifactReader Workspace { get; init; }
    public required IReadOnlyDictionary<string, LanguageAnalysisResult> Languages { get; init; }
    public required IFindingSink Findings { get; init; }
    public required DateOnly Today { get; init; }

    /// <summary>Per-file change history from the connector (empty when unavailable): churn, authors, last change.</summary>
    public IReadOnlyList<FileChangeFact> History { get; init; } = [];
}
