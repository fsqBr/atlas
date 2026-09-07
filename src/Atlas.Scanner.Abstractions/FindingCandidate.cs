using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;

namespace Atlas.Scanner.Abstractions;

/// <summary>
/// What a scanner emits. Identity (fingerprint) and lifecycle are decided later
/// by reconciliation — scanners only describe what they saw and where.
/// </summary>
public sealed record FindingCandidate(
    string RuleId,
    Severity Severity,
    ConfidenceLevel Confidence,
    string Title,
    string Message,
    EvidenceCandidate Evidence,
    string? Remediation = null,
    IReadOnlyDictionary<string, string>? Data = null);

/// <summary>
/// Location facts only. Symbol (or a snippet hash) is what makes two sightings
/// the same finding even when line numbers move; never a secret or PII value.
/// </summary>
public sealed record EvidenceCandidate(
    string? FilePath = null,
    int? LineStart = null,
    int? LineEnd = null,
    string? Symbol = null,
    string? SnippetHash = null);

public interface IFindingSink
{
    void Emit(FindingCandidate candidate);
}

/// <summary>Creates the contained reader a scan uses for a materialized workspace root.</summary>
public interface IArtifactReaderFactory
{
    IArtifactReader Create(string rootPath);

    /// <summary>Reader honoring per-assessment exclusion globs (plus defaults and the root .atlasignore).</summary>
    IArtifactReader Create(string rootPath, IReadOnlyList<string> excludeGlobs) => Create(rootPath);
}
