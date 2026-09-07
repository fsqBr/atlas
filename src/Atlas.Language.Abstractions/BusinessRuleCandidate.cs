using Atlas.Domain.Workspaces;

namespace Atlas.Language.Abstractions;

/// <summary>A method worth asking the model about: complex, decision-heavy, in domain-looking code.</summary>
public sealed record BusinessRuleCandidate(
    string FilePath,
    string Symbol,
    int StartLine,
    int EndLine,
    int Complexity,
    double Score,
    string Snippet);

/// <summary>Language adapters select candidates deterministically; the AI layer only ever sees what they pick.</summary>
public interface IBusinessRuleCandidateSource
{
    Task<IReadOnlyList<BusinessRuleCandidate>> FindAsync(IArtifactReader workspace, int max, CancellationToken cancellationToken);
}
